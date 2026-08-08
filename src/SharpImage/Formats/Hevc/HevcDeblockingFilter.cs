using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// HEVC in-loop deblocking filter.
    /// Reduces blocking artifacts at CTU, CU, and TU boundaries.
    /// </summary>
    /// <remarks>
    /// Key differences from H.264:
    /// - Filters only 8x8 grid boundaries (not 4x4)
    /// - Uses tc and beta tables instead of alpha/tc0
    /// - Boundary strength 0-2 (not 0-4)
    /// - Stronger filter mode for BS=2 (intra edges)
    /// - Parallel-friendly design (edges processed independently)
    /// </remarks>
    public static class HevcDeblockingFilter
    {
        #region Lookup Tables

        /// <summary>
        /// Beta table indexed by QP (0-51).
        /// Controls threshold for determining if filtering should occur.
        /// From HEVC Table 8-4.
        /// </summary>
        private static ReadOnlySpan<byte> BetaTable => new byte[]
        {
             0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
             6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24,
            26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56,
            58, 60, 62, 64
        };

        /// <summary>
        /// Tc table indexed by QP (0-53). Extended to handle offsets.
        /// Clipping threshold for edge filtering.
        /// From HEVC Table 8-5.
        /// </summary>
        private static ReadOnlySpan<byte> TcTable => new byte[]
        {
             0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
             0,  0,  1,  1,  1,  1,  1,  1,  1,  1,  1,  2,  2,  2,  2,  3,
             3,  3,  3,  4,  4,  4,  5,  5,  6,  6,  7,  8,  9, 10, 11, 13,
            14, 16, 18, 20, 22, 24
        };

        #endregion

        #region Boundary Strength Calculation

        /// <summary>
        /// Calculates boundary strength for a vertical or horizontal edge.
        /// </summary>
        /// <param name="pIsIntra">True if P block is intra-coded.</param>
        /// <param name="qIsIntra">True if Q block is intra-coded.</param>
        /// <param name="pHasResidual">True if P block has non-zero residual.</param>
        /// <param name="qHasResidual">True if Q block has non-zero residual.</param>
        /// <param name="pMvX">P block motion vector X in quarter-pixel.</param>
        /// <param name="pMvY">P block motion vector Y in quarter-pixel.</param>
        /// <param name="qMvX">Q block motion vector X in quarter-pixel.</param>
        /// <param name="qMvY">Q block motion vector Y in quarter-pixel.</param>
        /// <param name="pRefIdx">P block reference index.</param>
        /// <param name="qRefIdx">Q block reference index.</param>
        /// <returns>Boundary strength: 0, 1, or 2.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculateBoundaryStrength(
            bool pIsIntra,
            bool qIsIntra,
            bool pHasResidual,
            bool qHasResidual,
            int pMvX,
            int pMvY,
            int qMvX,
            int qMvY,
            int pRefIdx,
            int qRefIdx)
        {
            // BS = 2: Either block is intra-coded
            if (pIsIntra || qIsIntra)
                return 2;

            // BS = 1: Either block has non-zero residual
            if (pHasResidual || qHasResidual)
                return 1;

            // BS = 1: Different reference pictures
            if (pRefIdx != qRefIdx)
                return 1;

            // BS = 1: Motion vector difference >= 1 pixel (4 quarter-pel units)
            int mvDiffX = Math.Abs(pMvX - qMvX);
            int mvDiffY = Math.Abs(pMvY - qMvY);
            if (mvDiffX >= 4 || mvDiffY >= 4)
                return 1;

            // BS = 0: No filtering needed
            return 0;
        }

        /// <summary>
        /// Simplified boundary strength for intra-only edges.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculateBoundaryStrengthIntra(bool pIsIntra, bool qIsIntra)
        {
            return (pIsIntra || qIsIntra) ? 2 : 0;
        }

        #endregion

        #region Luma Filtering

        /// <summary>
        /// Applies deblocking filter to a vertical luma edge (processes 4 pixels vertically).
        /// </summary>
        /// <param name="samples">Luma plane buffer.</param>
        /// <param name="stride">Stride of luma plane.</param>
        /// <param name="x">X position of the edge (column).</param>
        /// <param name="y">Y position (top of 4-pixel edge).</param>
        /// <param name="qpP">QP of P block (left of edge).</param>
        /// <param name="qpQ">QP of Q block (right of edge).</param>
        /// <param name="boundaryStrength">BS value (0-2).</param>
        /// <param name="betaOffsetDiv2">Beta offset divided by 2.</param>
        /// <param name="tcOffsetDiv2">Tc offset divided by 2.</param>
        /// <param name="bitDepth">Bit depth (8, 10, or 12).</param>
        public static void FilterLumaEdgeVertical(
            Span<byte> samples,
            int stride,
            int x,
            int y,
            int qpP,
            int qpQ,
            int boundaryStrength,
            int betaOffsetDiv2,
            int tcOffsetDiv2,
            int bitDepth = 8)
        {
            if (boundaryStrength == 0)
                return;

            // Calculate average QP and lookup thresholds
            int qpL = (qpP + qpQ + 1) >> 1;
            int indexBeta = Math.Clamp(qpL + (betaOffsetDiv2 << 1), 0, 51);
            int indexTc = Math.Clamp(qpL + 2 * (boundaryStrength - 1) + (tcOffsetDiv2 << 1), 0, 53);
            
            int beta = BetaTable[indexBeta] << (bitDepth - 8);
            int tc = TcTable[indexTc] << (bitDepth - 8);

            if (tc == 0)
                return;

            // Process 4 rows (8x8 grid in HEVC deblocking)
            for (int row = 0; row < 4; row++)
            {
                int offset = (y + row) * stride + x;

                // Read pixels: p3 p2 p1 p0 | q0 q1 q2 q3
                int p3 = samples[offset - 4];
                int p2 = samples[offset - 3];
                int p1 = samples[offset - 2];
                int p0 = samples[offset - 1];
                int q0 = samples[offset];
                int q1 = samples[offset + 1];
                int q2 = samples[offset + 2];
                int q3 = samples[offset + 3];

                // Check if filtering should be applied (8.7.2.5.3)
                int d = Math.Abs(p2 - 2 * p1 + p0) + Math.Abs(q2 - 2 * q1 + q0);
                if (d >= beta)
                    continue;

                // Additional condition: |p0 - q0| < (5 * tc + 1) >> 1
                int pq0Diff = Math.Abs(p0 - q0);
                if (pq0Diff >= ((5 * tc + 1) >> 1))
                    continue;

                // Check for strong filtering mode (BS = 2)
                bool strongFilter = false;
                if (boundaryStrength == 2)
                {
                    int dp = Math.Abs(p2 - 2 * p1 + p0);
                    int dq = Math.Abs(q2 - 2 * q1 + q0);
                    int dpq = pq0Diff;
                    
                    strongFilter = (dp < (beta + (beta >> 2)) >> 3) &&
                                   (dq < (beta + (beta >> 2)) >> 3) &&
                                   (dpq < ((5 * tc + 1) >> 1));
                }

                if (strongFilter)
                {
                    // Strong filtering: modify 3 pixels on each side
                    int p0New = (p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3;
                    int p1New = (p2 + p1 + p0 + q0 + 2) >> 2;
                    int p2New = (2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3;
                    int q0New = (p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3;
                    int q1New = (p0 + q0 + q1 + q2 + 2) >> 2;
                    int q2New = (p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3;

                    samples[offset - 3] = (byte)Math.Clamp(p2New, 0, 255);
                    samples[offset - 2] = (byte)Math.Clamp(p1New, 0, 255);
                    samples[offset - 1] = (byte)Math.Clamp(p0New, 0, 255);
                    samples[offset] = (byte)Math.Clamp(q0New, 0, 255);
                    samples[offset + 1] = (byte)Math.Clamp(q1New, 0, 255);
                    samples[offset + 2] = (byte)Math.Clamp(q2New, 0, 255);
                }
                else
                {
                    // Weak filtering: modify 1-2 pixels on each side
                    int delta = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                    delta = Math.Clamp(delta, -tc, tc);

                    int p0New = p0 + delta;
                    int q0New = q0 - delta;
                    
                    samples[offset - 1] = (byte)Math.Clamp(p0New, 0, 255);
                    samples[offset] = (byte)Math.Clamp(q0New, 0, 255);

                    // Conditionally filter p1 and q1
                    int dp = Math.Abs(p2 - 2 * p1 + p0);
                    int dq = Math.Abs(q2 - 2 * q1 + q0);
                    int tcDiv2 = tc >> 1;

                    if (dp < ((beta + (beta >> 2)) >> 3))
                    {
                        int delta1 = (((p2 + p0 + 1) >> 1) - p1 + delta / 2);
                        delta1 = Math.Clamp(delta1, -tcDiv2, tcDiv2);
                        samples[offset - 2] = (byte)Math.Clamp(p1 + delta1, 0, 255);
                    }

                    if (dq < ((beta + (beta >> 2)) >> 3))
                    {
                        int delta1 = (((q2 + q0 + 1) >> 1) - q1 - delta / 2);
                        delta1 = Math.Clamp(delta1, -tcDiv2, tcDiv2);
                        samples[offset + 1] = (byte)Math.Clamp(q1 + delta1, 0, 255);
                    }
                }
            }
        }

        /// <summary>
        /// Applies deblocking filter to a horizontal luma edge.
        /// </summary>
        public static void FilterLumaEdgeHorizontal(
            Span<byte> samples,
            int stride,
            int x,
            int y,
            int qpP,
            int qpQ,
            int boundaryStrength,
            int betaOffsetDiv2,
            int tcOffsetDiv2,
            int bitDepth = 8)
        {
            if (boundaryStrength == 0)
                return;

            int qpL = (qpP + qpQ + 1) >> 1;
            int indexBeta = Math.Clamp(qpL + (betaOffsetDiv2 << 1), 0, 51);
            int indexTc = Math.Clamp(qpL + 2 * (boundaryStrength - 1) + (tcOffsetDiv2 << 1), 0, 53);
            
            int beta = BetaTable[indexBeta] << (bitDepth - 8);
            int tc = TcTable[indexTc] << (bitDepth - 8);

            if (tc == 0)
                return;

            // Process 4 columns
            for (int col = 0; col < 4; col++)
            {
                int xPos = x + col;
                
                // Read pixels vertically
                int p3 = samples[(y - 4) * stride + xPos];
                int p2 = samples[(y - 3) * stride + xPos];
                int p1 = samples[(y - 2) * stride + xPos];
                int p0 = samples[(y - 1) * stride + xPos];
                int q0 = samples[y * stride + xPos];
                int q1 = samples[(y + 1) * stride + xPos];
                int q2 = samples[(y + 2) * stride + xPos];
                int q3 = samples[(y + 3) * stride + xPos];

                int d = Math.Abs(p2 - 2 * p1 + p0) + Math.Abs(q2 - 2 * q1 + q0);
                if (d >= beta)
                    continue;

                int pq0Diff = Math.Abs(p0 - q0);
                if (pq0Diff >= ((5 * tc + 1) >> 1))
                    continue;

                bool strongFilter = false;
                if (boundaryStrength == 2)
                {
                    int dp = Math.Abs(p2 - 2 * p1 + p0);
                    int dq = Math.Abs(q2 - 2 * q1 + q0);
                    
                    strongFilter = (dp < (beta + (beta >> 2)) >> 3) &&
                                   (dq < (beta + (beta >> 2)) >> 3) &&
                                   (pq0Diff < ((5 * tc + 1) >> 1));
                }

                if (strongFilter)
                {
                    int p0New = (p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3;
                    int p1New = (p2 + p1 + p0 + q0 + 2) >> 2;
                    int p2New = (2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3;
                    int q0New = (p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3;
                    int q1New = (p0 + q0 + q1 + q2 + 2) >> 2;
                    int q2New = (p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3;

                    samples[(y - 3) * stride + xPos] = (byte)Math.Clamp(p2New, 0, 255);
                    samples[(y - 2) * stride + xPos] = (byte)Math.Clamp(p1New, 0, 255);
                    samples[(y - 1) * stride + xPos] = (byte)Math.Clamp(p0New, 0, 255);
                    samples[y * stride + xPos] = (byte)Math.Clamp(q0New, 0, 255);
                    samples[(y + 1) * stride + xPos] = (byte)Math.Clamp(q1New, 0, 255);
                    samples[(y + 2) * stride + xPos] = (byte)Math.Clamp(q2New, 0, 255);
                }
                else
                {
                    int delta = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                    delta = Math.Clamp(delta, -tc, tc);

                    samples[(y - 1) * stride + xPos] = (byte)Math.Clamp(p0 + delta, 0, 255);
                    samples[y * stride + xPos] = (byte)Math.Clamp(q0 - delta, 0, 255);

                    int dp = Math.Abs(p2 - 2 * p1 + p0);
                    int dq = Math.Abs(q2 - 2 * q1 + q0);
                    int tcDiv2 = tc >> 1;

                    if (dp < ((beta + (beta >> 2)) >> 3))
                    {
                        int delta1 = (((p2 + p0 + 1) >> 1) - p1 + delta / 2);
                        delta1 = Math.Clamp(delta1, -tcDiv2, tcDiv2);
                        samples[(y - 2) * stride + xPos] = (byte)Math.Clamp(p1 + delta1, 0, 255);
                    }

                    if (dq < ((beta + (beta >> 2)) >> 3))
                    {
                        int delta1 = (((q2 + q0 + 1) >> 1) - q1 - delta / 2);
                        delta1 = Math.Clamp(delta1, -tcDiv2, tcDiv2);
                        samples[(y + 1) * stride + xPos] = (byte)Math.Clamp(q1 + delta1, 0, 255);
                    }
                }
            }
        }

        #endregion

        #region Chroma Filtering

        /// <summary>
        /// Applies deblocking filter to a vertical chroma edge.
        /// Chroma only uses weak filtering and only modifies 1 pixel on each side.
        /// </summary>
        public static void FilterChromaEdgeVertical(
            Span<byte> samples,
            int stride,
            int x,
            int y,
            int qpP,
            int qpQ,
            int boundaryStrength,
            int tcOffsetDiv2,
            int chromaQpOffset,
            int bitDepth = 8)
        {
            if (boundaryStrength < 2)
                return;  // Chroma only filters BS=2 edges

            // Calculate chroma QP
            int qpL = (qpP + qpQ + 1) >> 1;
            int qpC = ChromaQpFromLuma(qpL + chromaQpOffset);
            int indexTc = Math.Clamp(qpC + 2 + (tcOffsetDiv2 << 1), 0, 53);
            int tc = TcTable[indexTc] << (bitDepth - 8);

            if (tc == 0)
                return;

            // Process 2 rows (4x4 chroma in 4:2:0)
            for (int row = 0; row < 2; row++)
            {
                int offset = (y + row) * stride + x;

                int p1 = samples[offset - 2];
                int p0 = samples[offset - 1];
                int q0 = samples[offset];
                int q1 = samples[offset + 1];

                int delta = (((q0 - p0) * 4) + (p1 - q1) + 4) >> 3;
                delta = Math.Clamp(delta, -tc, tc);

                samples[offset - 1] = (byte)Math.Clamp(p0 + delta, 0, 255);
                samples[offset] = (byte)Math.Clamp(q0 - delta, 0, 255);
            }
        }

        /// <summary>
        /// Applies deblocking filter to a horizontal chroma edge.
        /// </summary>
        public static void FilterChromaEdgeHorizontal(
            Span<byte> samples,
            int stride,
            int x,
            int y,
            int qpP,
            int qpQ,
            int boundaryStrength,
            int tcOffsetDiv2,
            int chromaQpOffset,
            int bitDepth = 8)
        {
            if (boundaryStrength < 2)
                return;

            int qpL = (qpP + qpQ + 1) >> 1;
            int qpC = ChromaQpFromLuma(qpL + chromaQpOffset);
            int indexTc = Math.Clamp(qpC + 2 + (tcOffsetDiv2 << 1), 0, 53);
            int tc = TcTable[indexTc] << (bitDepth - 8);

            if (tc == 0)
                return;

            for (int col = 0; col < 2; col++)
            {
                int xPos = x + col;

                int p1 = samples[(y - 2) * stride + xPos];
                int p0 = samples[(y - 1) * stride + xPos];
                int q0 = samples[y * stride + xPos];
                int q1 = samples[(y + 1) * stride + xPos];

                int delta = (((q0 - p0) * 4) + (p1 - q1) + 4) >> 3;
                delta = Math.Clamp(delta, -tc, tc);

                samples[(y - 1) * stride + xPos] = (byte)Math.Clamp(p0 + delta, 0, 255);
                samples[y * stride + xPos] = (byte)Math.Clamp(q0 - delta, 0, 255);
            }
        }

        #endregion

        #region CTU Filtering

        /// <summary>
        /// Applies deblocking filter to all edges within a CTU.
        /// </summary>
        /// <param name="luma">Luma plane.</param>
        /// <param name="lumaStride">Luma stride.</param>
        /// <param name="chromaCb">Cb plane.</param>
        /// <param name="chromaCr">Cr plane.</param>
        /// <param name="chromaStride">Chroma stride.</param>
        /// <param name="ctuX">CTU X index.</param>
        /// <param name="ctuY">CTU Y index.</param>
        /// <param name="ctuSize">CTU size (16, 32, or 64).</param>
        /// <param name="boundaryStrengthMap">Map of BS values for 8x8 blocks.</param>
        /// <param name="qpMap">Map of QP values for 8x8 blocks.</param>
        /// <param name="betaOffsetDiv2">Beta offset from PPS/slice.</param>
        /// <param name="tcOffsetDiv2">Tc offset from PPS/slice.</param>
        /// <param name="chromaQpOffset">Chroma QP offset.</param>
        public static void FilterCtu(
            Span<byte> luma,
            int lumaStride,
            Span<byte> chromaCb,
            Span<byte> chromaCr,
            int chromaStride,
            int ctuX,
            int ctuY,
            int ctuSize,
            ReadOnlySpan<byte> boundaryStrengthMap,
            ReadOnlySpan<byte> qpMap,
            int betaOffsetDiv2,
            int tcOffsetDiv2,
            int chromaQpOffset)
        {
            int ctuPixelX = ctuX * ctuSize;
            int ctuPixelY = ctuY * ctuSize;
            int blocksPerCtu = ctuSize / 8;

            // Filter vertical edges (8-pixel grid)
            for (int by = 0; by < blocksPerCtu; by++)
            {
                for (int bx = 1; bx < blocksPerCtu; bx++)
                {
                    int blockIndex = by * blocksPerCtu + bx;
                    int prevBlockIndex = by * blocksPerCtu + bx - 1;
                    
                    int bs = boundaryStrengthMap[blockIndex];
                    int qpP = qpMap[prevBlockIndex];
                    int qpQ = qpMap[blockIndex];

                    if (bs > 0)
                    {
                        int x = ctuPixelX + bx * 8;
                        int y = ctuPixelY + by * 8;

                        // Filter 4 rows at a time (covers 8 pixel block)
                        FilterLumaEdgeVertical(luma, lumaStride, x, y, qpP, qpQ, bs,
                            betaOffsetDiv2, tcOffsetDiv2);
                        FilterLumaEdgeVertical(luma, lumaStride, x, y + 4, qpP, qpQ, bs,
                            betaOffsetDiv2, tcOffsetDiv2);

                        // Chroma (4x4 in 4:2:0)
                        if ((bx & 1) == 0)  // 16-pixel grid for chroma
                        {
                            int cx = (ctuPixelX + bx * 8) / 2;
                            int cy = (ctuPixelY + by * 8) / 2;
                            
                            FilterChromaEdgeVertical(chromaCb, chromaStride, cx, cy,
                                qpP, qpQ, bs, tcOffsetDiv2, chromaQpOffset);
                            FilterChromaEdgeVertical(chromaCr, chromaStride, cx, cy,
                                qpP, qpQ, bs, tcOffsetDiv2, chromaQpOffset);
                        }
                    }
                }
            }

            // Filter horizontal edges
            for (int by = 1; by < blocksPerCtu; by++)
            {
                for (int bx = 0; bx < blocksPerCtu; bx++)
                {
                    int blockIndex = by * blocksPerCtu + bx;
                    int aboveBlockIndex = (by - 1) * blocksPerCtu + bx;
                    
                    int bs = boundaryStrengthMap[blockIndex];
                    int qpP = qpMap[aboveBlockIndex];
                    int qpQ = qpMap[blockIndex];

                    if (bs > 0)
                    {
                        int x = ctuPixelX + bx * 8;
                        int y = ctuPixelY + by * 8;

                        FilterLumaEdgeHorizontal(luma, lumaStride, x, y, qpP, qpQ, bs,
                            betaOffsetDiv2, tcOffsetDiv2);
                        FilterLumaEdgeHorizontal(luma, lumaStride, x + 4, y, qpP, qpQ, bs,
                            betaOffsetDiv2, tcOffsetDiv2);

                        if ((by & 1) == 0)
                        {
                            int cx = (ctuPixelX + bx * 8) / 2;
                            int cy = (ctuPixelY + by * 8) / 2;
                            
                            FilterChromaEdgeHorizontal(chromaCb, chromaStride, cx, cy,
                                qpP, qpQ, bs, tcOffsetDiv2, chromaQpOffset);
                            FilterChromaEdgeHorizontal(chromaCr, chromaStride, cx, cy,
                                qpP, qpQ, bs, tcOffsetDiv2, chromaQpOffset);
                        }
                    }
                }
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Converts luma QP to chroma QP using HEVC mapping table.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ChromaQpFromLuma(int qpL)
        {
            return ChromaQpFromLumaPublic(qpL);
        }

        /// <summary>
        /// Converts luma QP to chroma QP using HEVC mapping table (public for use by HevcDecoder).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ChromaQpFromLumaPublic(int qpL, bool is420 = true)
        {
            if (!is420)
            {
                // 422/444: QpC = clip(qPi, 0, 51) — no table mapping (FFmpeg filter.c:69-70)
                return Math.Clamp(qpL, 0, 51);
            }

            // HEVC Table 8-6: qPi to qPc mapping (420 only)
            if (qpL < 30)
                return qpL;
            if (qpL > 43)
                return qpL - 6;
            
            // Mapping for qpL 30-43
            ReadOnlySpan<byte> mapping = new byte[]
            {
                29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37
            };
            return mapping[qpL - 30];
        }

        /// <summary>
        /// Gets the beta threshold for a given QP.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetBeta(int qp, int betaOffsetDiv2, int bitDepth = 8)
        {
            int index = Math.Clamp(qp + (betaOffsetDiv2 << 1), 0, 51);
            return BetaTable[index] << (bitDepth - 8);
        }

        /// <summary>
        /// Gets the tc threshold for a given QP and boundary strength.
        /// Matches FFmpeg's TC_CALC macro: tctable[clamp(qp + 2*(bs-1) + tc_offset, 0, 53)]
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetTc(int qp, int boundaryStrength, int tcOffsetDiv2, int bitDepth = 8)
        {
            int index = Math.Clamp(qp + 2 * (boundaryStrength - 1) + (tcOffsetDiv2 << 1), 0, 53);
            return TcTable[index] << (bitDepth - 8);
        }

        /// <summary>
        /// Gets the chroma tc for deblocking.
        /// Matches FFmpeg's chroma_tc() in filter.c: maps luma QP to chroma QP,
        /// adds DEFAULT_INTRA_TC_OFFSET=2, then looks up tc table.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetChromaTc(int qpY, int chromaQpOffset, int tcOffsetDiv2, int bitDepth = 8, bool is420 = true)
        {
            int qpC = ChromaQpFromLumaPublic(qpY + chromaQpOffset, is420);
            int index = Math.Clamp(qpC + 2 + (tcOffsetDiv2 << 1), 0, 53);
            return TcTable[index] << (bitDepth - 8);
        }

        #endregion

        #region 8-bit 8-Row Filter Functions

        /// <summary>
        /// 8-bit vertical luma edge deblocking filter.
        /// Processes a full 8-row edge in two 4-row halves, each with its own tc.
        /// Uses rows 0 and 3 for beta/strong/weak decision per sub-block (matches FFmpeg exactly).
        /// </summary>
        public static void FilterLumaEdge8RowVertical(
            Span<byte> samples, int stride, int x, int y,
            int beta, int tc0, int tc1,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int yBase = y + j * 4;

                int off0 = yBase * stride + x;
                int off3 = (yBase + 3) * stride + x;

                int p0r0 = samples[off0 - 1], p1r0 = samples[off0 - 2], p2r0 = samples[off0 - 3], p3r0 = samples[off0 - 4];
                int q0r0 = samples[off0], q1r0 = samples[off0 + 1], q2r0 = samples[off0 + 2], q3r0 = samples[off0 + 3];
                int p0r3 = samples[off3 - 1], p1r3 = samples[off3 - 2], p2r3 = samples[off3 - 3], p3r3 = samples[off3 - 4];
                int q0r3 = samples[off3], q1r3 = samples[off3 + 1], q2r3 = samples[off3 + 2], q3r3 = samples[off3 + 3];

                int dp0 = Math.Abs(p2r0 - 2 * p1r0 + p0r0);
                int dq0 = Math.Abs(q2r0 - 2 * q1r0 + q0r0);
                int dp3 = Math.Abs(p2r3 - 2 * p1r3 + p0r3);
                int dq3 = Math.Abs(q2r3 - 2 * q1r3 + q0r3);
                int d0 = dp0 + dq0;
                int d3 = dp3 + dq3;

                if (d0 + d3 >= beta) continue;

                int beta_2 = beta >> 2;
                int beta_3 = beta >> 3;
                int tc5 = (5 * tc + 1) >> 1;

                bool strong = (d0 << 1) < beta_2 && (d3 << 1) < beta_2 &&
                              Math.Abs(p3r0 - p0r0) + Math.Abs(q3r0 - q0r0) < beta_3 &&
                              Math.Abs(p0r0 - q0r0) < tc5 &&
                              Math.Abs(p3r3 - p0r3) + Math.Abs(q3r3 - q0r3) < beta_3 &&
                              Math.Abs(p0r3 - q0r3) < tc5;

                if (strong)
                {
                    int tc2 = tc * 2;
                    for (int d = 0; d < 4; d++)
                    {
                        int off = (yBase + d) * stride + x;
                        int p3 = samples[off - 4];
                        int p2 = samples[off - 3];
                        int p1 = samples[off - 2];
                        int p0 = samples[off - 1];
                        int q0 = samples[off];
                        int q1 = samples[off + 1];
                        int q2 = samples[off + 2];
                        int q3 = samples[off + 3];

                        if (!noP)
                        {
                            samples[off - 1] = (byte)Math.Clamp(p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, 255);
                            samples[off - 2] = (byte)Math.Clamp(p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, 255);
                            samples[off - 3] = (byte)Math.Clamp(p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, 255);
                        }
                        if (!noQ)
                        {
                            samples[off] = (byte)Math.Clamp(q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, 255);
                            samples[off + 1] = (byte)Math.Clamp(q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, 255);
                            samples[off + 2] = (byte)Math.Clamp(q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, 255);
                        }
                    }
                }
                else
                {
                    int ndThreshold = (beta + (beta >> 1)) >> 3;
                    bool extendP = dp0 + dp3 < ndThreshold;
                    bool extendQ = dq0 + dq3 < ndThreshold;
                    int tcDiv2 = tc >> 1;

                    for (int d = 0; d < 4; d++)
                    {
                        int off = (yBase + d) * stride + x;
                        int p2 = samples[off - 3];
                        int p1 = samples[off - 2];
                        int p0 = samples[off - 1];
                        int q0 = samples[off];
                        int q1 = samples[off + 1];
                        int q2 = samples[off + 2];

                        int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                        if (Math.Abs(delta0) < 10 * tc)
                        {
                            delta0 = Math.Clamp(delta0, -tc, tc);
                            if (!noP)
                                samples[off - 1] = (byte)Math.Clamp(p0 + delta0, 0, 255);
                            if (!noQ)
                                samples[off] = (byte)Math.Clamp(q0 - delta0, 0, 255);

                            if (extendP && !noP)
                            {
                                int deltap1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[off - 2] = (byte)Math.Clamp(p1 + deltap1, 0, 255);
                            }
                            if (extendQ && !noQ)
                            {
                                int deltaq1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[off + 1] = (byte)Math.Clamp(q1 + deltaq1, 0, 255);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 8-bit horizontal luma edge deblocking filter.
        /// Processes a full 8-column edge in two 4-column halves, each with its own tc.
        /// Uses columns 0 and 3 for beta/strong/weak decision per sub-block (matches FFmpeg exactly).
        /// </summary>
        public static void FilterLumaEdge8RowHorizontal(
            Span<byte> samples, int stride, int x, int y,
            int beta, int tc0, int tc1,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int xBase = x + j * 4;

                int p0c0 = samples[(y - 1) * stride + xBase], p1c0 = samples[(y - 2) * stride + xBase], p2c0 = samples[(y - 3) * stride + xBase], p3c0 = samples[(y - 4) * stride + xBase];
                int q0c0 = samples[y * stride + xBase], q1c0 = samples[(y + 1) * stride + xBase], q2c0 = samples[(y + 2) * stride + xBase], q3c0 = samples[(y + 3) * stride + xBase];
                int p0c3 = samples[(y - 1) * stride + xBase + 3], p1c3 = samples[(y - 2) * stride + xBase + 3], p2c3 = samples[(y - 3) * stride + xBase + 3], p3c3 = samples[(y - 4) * stride + xBase + 3];
                int q0c3 = samples[y * stride + xBase + 3], q1c3 = samples[(y + 1) * stride + xBase + 3], q2c3 = samples[(y + 2) * stride + xBase + 3], q3c3 = samples[(y + 3) * stride + xBase + 3];

                int dp0 = Math.Abs(p2c0 - 2 * p1c0 + p0c0);
                int dq0 = Math.Abs(q2c0 - 2 * q1c0 + q0c0);
                int dp3 = Math.Abs(p2c3 - 2 * p1c3 + p0c3);
                int dq3 = Math.Abs(q2c3 - 2 * q1c3 + q0c3);
                int d0 = dp0 + dq0;
                int d3 = dp3 + dq3;

                if (d0 + d3 >= beta) continue;

                int beta_2 = beta >> 2;
                int beta_3 = beta >> 3;
                int tc5 = (5 * tc + 1) >> 1;

                bool strong = (d0 << 1) < beta_2 && (d3 << 1) < beta_2 &&
                              Math.Abs(p3c0 - p0c0) + Math.Abs(q3c0 - q0c0) < beta_3 &&
                              Math.Abs(p0c0 - q0c0) < tc5 &&
                              Math.Abs(p3c3 - p0c3) + Math.Abs(q3c3 - q0c3) < beta_3 &&
                              Math.Abs(p0c3 - q0c3) < tc5;

                if (strong)
                {
                    int tc2 = tc * 2;
                    for (int d = 0; d < 4; d++)
                    {
                        int col = xBase + d;
                        int p3 = samples[(y - 4) * stride + col];
                        int p2 = samples[(y - 3) * stride + col];
                        int p1 = samples[(y - 2) * stride + col];
                        int p0 = samples[(y - 1) * stride + col];
                        int q0 = samples[y * stride + col];
                        int q1 = samples[(y + 1) * stride + col];
                        int q2 = samples[(y + 2) * stride + col];
                        int q3 = samples[(y + 3) * stride + col];

                        if (!noP)
                        {
                            samples[(y - 1) * stride + col] = (byte)Math.Clamp(p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, 255);
                            samples[(y - 2) * stride + col] = (byte)Math.Clamp(p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, 255);
                            samples[(y - 3) * stride + col] = (byte)Math.Clamp(p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, 255);
                        }
                        if (!noQ)
                        {
                            samples[y * stride + col] = (byte)Math.Clamp(q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, 255);
                            samples[(y + 1) * stride + col] = (byte)Math.Clamp(q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, 255);
                            samples[(y + 2) * stride + col] = (byte)Math.Clamp(q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, 255);
                        }
                    }
                }
                else
                {
                    int ndThreshold = (beta + (beta >> 1)) >> 3;
                    bool extendP = dp0 + dp3 < ndThreshold;
                    bool extendQ = dq0 + dq3 < ndThreshold;
                    int tcDiv2 = tc >> 1;

                    for (int d = 0; d < 4; d++)
                    {
                        int col = xBase + d;
                        int p2 = samples[(y - 3) * stride + col];
                        int p1 = samples[(y - 2) * stride + col];
                        int p0 = samples[(y - 1) * stride + col];
                        int q0 = samples[y * stride + col];
                        int q1 = samples[(y + 1) * stride + col];
                        int q2 = samples[(y + 2) * stride + col];

                        int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                        if (Math.Abs(delta0) < 10 * tc)
                        {
                            delta0 = Math.Clamp(delta0, -tc, tc);
                            if (!noP)
                                samples[(y - 1) * stride + col] = (byte)Math.Clamp(p0 + delta0, 0, 255);
                            if (!noQ)
                                samples[y * stride + col] = (byte)Math.Clamp(q0 - delta0, 0, 255);

                            if (extendP && !noP)
                            {
                                int deltap1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[(y - 2) * stride + col] = (byte)Math.Clamp(p1 + deltap1, 0, 255);
                            }
                            if (extendQ && !noQ)
                            {
                                int deltaq1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[(y + 1) * stride + col] = (byte)Math.Clamp(q1 + deltaq1, 0, 255);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 8-bit vertical chroma edge deblocking filter.
        /// Processes 8 chroma rows in two 4-row halves with separate tc values.
        /// </summary>
        public static void FilterChromaEdge8RowVertical(
            Span<byte> samples, int stride, int x, int y,
            int tc0, int tc1,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                for (int d = 0; d < 4; d++)
                {
                    int off = (y + j * 4 + d) * stride + x;
                    int p1 = samples[off - 2];
                    int p0 = samples[off - 1];
                    int q0 = samples[off];
                    int q1 = samples[off + 1];

                    int delta = Math.Clamp(((q0 - p0) * 4 + p1 - q1 + 4) >> 3, -tc, tc);
                    if (!noP)
                        samples[off - 1] = (byte)Math.Clamp(p0 + delta, 0, 255);
                    if (!noQ)
                        samples[off] = (byte)Math.Clamp(q0 - delta, 0, 255);
                }
            }
        }

        /// <summary>
        /// 8-bit horizontal chroma edge deblocking filter.
        /// Processes 8 chroma columns in two 4-column halves with separate tc values.
        /// </summary>
        public static void FilterChromaEdge8RowHorizontal(
            Span<byte> samples, int stride, int x, int y,
            int tc0, int tc1, int validColumns = 8,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int groupColumns = Math.Min(4, validColumns - j * 4);
                for (int d = 0; d < groupColumns; d++)
                {
                    int col = x + j * 4 + d;
                    int p1 = samples[(y - 2) * stride + col];
                    int p0 = samples[(y - 1) * stride + col];
                    int q0 = samples[y * stride + col];
                    int q1 = samples[(y + 1) * stride + col];

                    int delta = Math.Clamp(((q0 - p0) * 4 + p1 - q1 + 4) >> 3, -tc, tc);
                    if (!noP)
                        samples[(y - 1) * stride + col] = (byte)Math.Clamp(p0 + delta, 0, 255);
                    if (!noQ)
                        samples[y * stride + col] = (byte)Math.Clamp(q0 - delta, 0, 255);
                }
            }
        }

        #endregion

        #region High Bit Depth Support

        /// <summary>
        /// High-bit-depth vertical luma edge deblocking filter.
        /// Processes a full 8-row edge in two 4-row halves, each with its own tc.
        /// Matches FFmpeg's hevc_loop_filter_luma from dsp_template.c exactly.
        /// </summary>
        /// <param name="samples">Luma plane as ushort.</param>
        /// <param name="stride">Row stride in samples (not bytes).</param>
        /// <param name="x">X position of the edge (first Q sample column).</param>
        /// <param name="y">Y position of the top of the 8-row edge.</param>
        /// <param name="beta">Beta threshold (already scaled for bit depth).</param>
        /// <param name="tc0">Tc for the top 4 rows (0 means skip).</param>
        /// <param name="tc1">Tc for the bottom 4 rows (0 means skip).</param>
        /// <param name="bitDepth">Bit depth (8, 10, or 12).</param>
        public static void FilterLumaEdge8RowVerticalHighBitDepth(
            Span<ushort> samples, int stride, int x, int y,
            int beta, int tc0, int tc1, int bitDepth,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            int maxValue = (1 << bitDepth) - 1;

            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int yBase = y + j * 4;

                // Read pixels at rows 0 and 3 of this half
                int off0 = yBase * stride + x;
                int off3 = (yBase + 3) * stride + x;

                int p0r0 = samples[off0 - 1], p1r0 = samples[off0 - 2], p2r0 = samples[off0 - 3], p3r0 = samples[off0 - 4];
                int q0r0 = samples[off0], q1r0 = samples[off0 + 1], q2r0 = samples[off0 + 2], q3r0 = samples[off0 + 3];
                int p0r3 = samples[off3 - 1], p1r3 = samples[off3 - 2], p2r3 = samples[off3 - 3], p3r3 = samples[off3 - 4];
                int q0r3 = samples[off3], q1r3 = samples[off3 + 1], q2r3 = samples[off3 + 2], q3r3 = samples[off3 + 3];

                // Compute d metrics at rows 0 and 3
                int dp0 = Math.Abs(p2r0 - 2 * p1r0 + p0r0);
                int dq0 = Math.Abs(q2r0 - 2 * q1r0 + q0r0);
                int dp3 = Math.Abs(p2r3 - 2 * p1r3 + p0r3);
                int dq3 = Math.Abs(q2r3 - 2 * q1r3 + q0r3);
                int d0 = dp0 + dq0;
                int d3 = dp3 + dq3;

                if (d0 + d3 >= beta) continue;

                int beta_2 = beta >> 2;
                int beta_3 = beta >> 3;
                int tc5 = (5 * tc + 1) >> 1;

                // Check strong filter: spec 8.7.2.5.6 — d0 and d3 checked INDIVIDUALLY
                bool strong = (d0 << 1) < beta_2 && (d3 << 1) < beta_2 &&
                              Math.Abs(p3r0 - p0r0) + Math.Abs(q3r0 - q0r0) < beta_3 &&
                              Math.Abs(p0r0 - q0r0) < tc5 &&
                              Math.Abs(p3r3 - p0r3) + Math.Abs(q3r3 - q0r3) < beta_3 &&
                              Math.Abs(p0r3 - q0r3) < tc5;

                if (strong)
                {
                    // HEVC spec 8.7.2.5.7: strong filter clips ALL positions to ±2*tC
                    int tc2 = tc * 2;

                    for (int d = 0; d < 4; d++)
                    {
                        int off = (yBase + d) * stride + x;
                        int p3 = samples[off - 4];
                        int p2 = samples[off - 3];
                        int p1 = samples[off - 2];
                        int p0 = samples[off - 1];
                        int q0 = samples[off];
                        int q1 = samples[off + 1];
                        int q2 = samples[off + 2];
                        int q3 = samples[off + 3];

                        if (!noP)
                        {
                            samples[off - 1] = (ushort)Math.Clamp(p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, maxValue);
                            samples[off - 2] = (ushort)Math.Clamp(p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, maxValue);
                            samples[off - 3] = (ushort)Math.Clamp(p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, maxValue);
                        }
                        if (!noQ)
                        {
                            samples[off] = (ushort)Math.Clamp(q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, maxValue);
                            samples[off + 1] = (ushort)Math.Clamp(q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, maxValue);
                            samples[off + 2] = (ushort)Math.Clamp(q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, maxValue);
                        }
                    }
                }
                else
                {
                    // Weak filter: nd_p/nd_q decided once for all 4 rows
                    int ndThreshold = (beta + (beta >> 1)) >> 3;
                    bool extendP = dp0 + dp3 < ndThreshold;
                    bool extendQ = dq0 + dq3 < ndThreshold;
                    int tcDiv2 = tc >> 1;

                    for (int d = 0; d < 4; d++)
                    {
                        int off = (yBase + d) * stride + x;
                        int p2 = samples[off - 3];
                        int p1 = samples[off - 2];
                        int p0 = samples[off - 1];
                        int q0 = samples[off];
                        int q1 = samples[off + 1];
                        int q2 = samples[off + 2];

                        int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                        if (Math.Abs(delta0) < 10 * tc)
                        {
                            delta0 = Math.Clamp(delta0, -tc, tc);
                            if (!noP)
                                samples[off - 1] = (ushort)Math.Clamp(p0 + delta0, 0, maxValue);
                            if (!noQ)
                                samples[off] = (ushort)Math.Clamp(q0 - delta0, 0, maxValue);

                            if (extendP && !noP)
                            {
                                int deltap1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[off - 2] = (ushort)Math.Clamp(p1 + deltap1, 0, maxValue);
                            }
                            if (extendQ && !noQ)
                            {
                                int deltaq1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[off + 1] = (ushort)Math.Clamp(q1 + deltaq1, 0, maxValue);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// High-bit-depth horizontal luma edge deblocking filter.
        /// Processes a full 8-column edge in two 4-column halves, each with its own tc.
        /// Matches FFmpeg's hevc_loop_filter_luma (horizontal orientation).
        /// </summary>
        public static void FilterLumaEdge8RowHorizontalHighBitDepth(
            Span<ushort> samples, int stride, int x, int y,
            int beta, int tc0, int tc1, int bitDepth,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            int maxValue = (1 << bitDepth) - 1;

            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int xBase = x + j * 4;

                // Read pixels at columns 0 and 3 of this half
                int p0c0 = samples[(y - 1) * stride + xBase], p1c0 = samples[(y - 2) * stride + xBase], p2c0 = samples[(y - 3) * stride + xBase], p3c0 = samples[(y - 4) * stride + xBase];
                int q0c0 = samples[y * stride + xBase], q1c0 = samples[(y + 1) * stride + xBase], q2c0 = samples[(y + 2) * stride + xBase], q3c0 = samples[(y + 3) * stride + xBase];
                int p0c3 = samples[(y - 1) * stride + xBase + 3], p1c3 = samples[(y - 2) * stride + xBase + 3], p2c3 = samples[(y - 3) * stride + xBase + 3], p3c3 = samples[(y - 4) * stride + xBase + 3];
                int q0c3 = samples[y * stride + xBase + 3], q1c3 = samples[(y + 1) * stride + xBase + 3], q2c3 = samples[(y + 2) * stride + xBase + 3], q3c3 = samples[(y + 3) * stride + xBase + 3];

                int dp0 = Math.Abs(p2c0 - 2 * p1c0 + p0c0);
                int dq0 = Math.Abs(q2c0 - 2 * q1c0 + q0c0);
                int dp3 = Math.Abs(p2c3 - 2 * p1c3 + p0c3);
                int dq3 = Math.Abs(q2c3 - 2 * q1c3 + q0c3);
                int d0 = dp0 + dq0;
                int d3 = dp3 + dq3;

                if (d0 + d3 >= beta) continue;

                int beta_2 = beta >> 2;
                int beta_3 = beta >> 3;
                int tc5 = (5 * tc + 1) >> 1;

                // Check strong filter: spec 8.7.2.5.6 — d0 and d3 checked INDIVIDUALLY
                bool strong = (d0 << 1) < beta_2 && (d3 << 1) < beta_2 &&
                              Math.Abs(p3c0 - p0c0) + Math.Abs(q3c0 - q0c0) < beta_3 &&
                              Math.Abs(p0c0 - q0c0) < tc5 &&
                              Math.Abs(p3c3 - p0c3) + Math.Abs(q3c3 - q0c3) < beta_3 &&
                              Math.Abs(p0c3 - q0c3) < tc5;

                if (strong)
                {
                    // HEVC spec 8.7.2.5.7: strong filter clips ALL positions to ±2*tC
                    int tc2 = tc * 2;

                    for (int d = 0; d < 4; d++)
                    {
                        int col = xBase + d;
                        int p3 = samples[(y - 4) * stride + col];
                        int p2 = samples[(y - 3) * stride + col];
                        int p1 = samples[(y - 2) * stride + col];
                        int p0 = samples[(y - 1) * stride + col];
                        int q0 = samples[y * stride + col];
                        int q1 = samples[(y + 1) * stride + col];
                        int q2 = samples[(y + 2) * stride + col];
                        int q3 = samples[(y + 3) * stride + col];

                        if (!noP)
                        {
                            samples[(y - 1) * stride + col] = (ushort)Math.Clamp(p0 + Math.Clamp(((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3) - p0, -tc2, tc2), 0, maxValue);
                            samples[(y - 2) * stride + col] = (ushort)Math.Clamp(p1 + Math.Clamp(((p2 + p1 + p0 + q0 + 2) >> 2) - p1, -tc2, tc2), 0, maxValue);
                            samples[(y - 3) * stride + col] = (ushort)Math.Clamp(p2 + Math.Clamp(((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3) - p2, -tc2, tc2), 0, maxValue);
                        }
                        if (!noQ)
                        {
                            samples[y * stride + col] = (ushort)Math.Clamp(q0 + Math.Clamp(((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3) - q0, -tc2, tc2), 0, maxValue);
                            samples[(y + 1) * stride + col] = (ushort)Math.Clamp(q1 + Math.Clamp(((p0 + q0 + q1 + q2 + 2) >> 2) - q1, -tc2, tc2), 0, maxValue);
                            samples[(y + 2) * stride + col] = (ushort)Math.Clamp(q2 + Math.Clamp(((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3) - q2, -tc2, tc2), 0, maxValue);
                        }
                    }
                }
                else
                {
                    int ndThreshold = (beta + (beta >> 1)) >> 3;
                    bool extendP = dp0 + dp3 < ndThreshold;
                    bool extendQ = dq0 + dq3 < ndThreshold;
                    int tcDiv2 = tc >> 1;

                    for (int d = 0; d < 4; d++)
                    {
                        int col = xBase + d;
                        int p2 = samples[(y - 3) * stride + col];
                        int p1 = samples[(y - 2) * stride + col];
                        int p0 = samples[(y - 1) * stride + col];
                        int q0 = samples[y * stride + col];
                        int q1 = samples[(y + 1) * stride + col];
                        int q2 = samples[(y + 2) * stride + col];

                        int delta0 = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                        if (Math.Abs(delta0) < 10 * tc)
                        {
                            delta0 = Math.Clamp(delta0, -tc, tc);
                            if (!noP)
                                samples[(y - 1) * stride + col] = (ushort)Math.Clamp(p0 + delta0, 0, maxValue);
                            if (!noQ)
                                samples[y * stride + col] = (ushort)Math.Clamp(q0 - delta0, 0, maxValue);

                            if (extendP && !noP)
                            {
                                int deltap1 = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[(y - 2) * stride + col] = (ushort)Math.Clamp(p1 + deltap1, 0, maxValue);
                            }
                            if (extendQ && !noQ)
                            {
                                int deltaq1 = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta0) >> 1, -tcDiv2, tcDiv2);
                                samples[(y + 1) * stride + col] = (ushort)Math.Clamp(q1 + deltaq1, 0, maxValue);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// High-bit-depth vertical chroma edge deblocking filter.
        /// Processes 8 chroma rows in two 4-row halves with separate tc values.
        /// Matches FFmpeg's hevc_loop_filter_chroma from dsp_template.c.
        /// </summary>
        public static void FilterChromaEdge8RowVerticalHighBitDepth(
            Span<ushort> samples, int stride, int x, int y,
            int tc0, int tc1, int bitDepth,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            int maxValue = (1 << bitDepth) - 1;

            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                for (int d = 0; d < 4; d++)
                {
                    int off = (y + j * 4 + d) * stride + x;
                    int p1 = samples[off - 2];
                    int p0 = samples[off - 1];
                    int q0 = samples[off];
                    int q1 = samples[off + 1];

                    int delta = Math.Clamp(((q0 - p0) * 4 + p1 - q1 + 4) >> 3, -tc, tc);
                    if (!noP)
                        samples[off - 1] = (ushort)Math.Clamp(p0 + delta, 0, maxValue);
                    if (!noQ)
                        samples[off] = (ushort)Math.Clamp(q0 - delta, 0, maxValue);
                }
            }
        }

        /// <summary>
        /// High-bit-depth horizontal chroma edge deblocking filter.
        /// Processes 8 chroma columns in two 4-column halves with separate tc values.
        /// Matches FFmpeg's hevc_loop_filter_chroma (horizontal orientation).
        /// </summary>
        public static void FilterChromaEdge8RowHorizontalHighBitDepth(
            Span<ushort> samples, int stride, int x, int y,
            int tc0, int tc1, int bitDepth, int validColumns = 8,
            bool noP0 = false, bool noQ0 = false, bool noP1 = false, bool noQ1 = false)
        {
            int maxValue = (1 << bitDepth) - 1;

            for (int j = 0; j < 2; j++)
            {
                int tc = j == 0 ? tc0 : tc1;
                if (tc <= 0) continue;

                bool noP = j == 0 ? noP0 : noP1;
                bool noQ = j == 0 ? noQ0 : noQ1;

                int groupColumns = Math.Min(4, validColumns - j * 4);
                for (int d = 0; d < groupColumns; d++)
                {
                    int col = x + j * 4 + d;
                    int p1 = samples[(y - 2) * stride + col];
                    int p0 = samples[(y - 1) * stride + col];
                    int q0 = samples[y * stride + col];
                    int q1 = samples[(y + 1) * stride + col];

                    int delta = Math.Clamp(((q0 - p0) * 4 + p1 - q1 + 4) >> 3, -tc, tc);
                    if (!noP)
                        samples[(y - 1) * stride + col] = (ushort)Math.Clamp(p0 + delta, 0, maxValue);
                    if (!noQ)
                        samples[y * stride + col] = (ushort)Math.Clamp(q0 - delta, 0, maxValue);
                }
            }
        }

        #endregion
    }
}
