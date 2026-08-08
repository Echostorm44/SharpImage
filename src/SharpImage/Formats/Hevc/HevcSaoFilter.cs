using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// Sample Adaptive Offset (SAO) filter for HEVC/H.265.
    /// SAO is a post-deblocking filter that reduces quantization artifacts
    /// by adding offsets to pixel values based on classification.
    /// </summary>
    /// <remarks>
    /// HEVC SAO operates at the CTU level with two modes:
    /// - Band Offset (BO): Groups pixels by intensity bands
    /// - Edge Offset (EO): Groups pixels by edge pattern
    /// </remarks>
    public static class HevcSaoFilter
    {
        /// <summary>
        /// Edge offset direction types.
        /// </summary>
        public enum SaoEdgeDirection
        {
            /// <summary>Horizontal (left-right neighbors).</summary>
            Horizontal = 0,
            /// <summary>Vertical (top-bottom neighbors).</summary>
            Vertical = 1,
            /// <summary>135° diagonal (top-left to bottom-right).</summary>
            Diagonal135 = 2,
            /// <summary>45° diagonal (top-right to bottom-left).</summary>
            Diagonal45 = 3
        }

        /// <summary>
        /// Edge offset category/class.
        /// </summary>
        public enum SaoEdgeCategory
        {
            /// <summary>Current pixel is a local minimum (concave).</summary>
            Concave = 0,
            /// <summary>Current pixel is less than one neighbor.</summary>
            Valley = 1,
            /// <summary>Current pixel equals both neighbors (flat).</summary>
            Flat = 2,
            /// <summary>Current pixel is greater than one neighbor.</summary>
            Peak = 3,
            /// <summary>Current pixel is a local maximum (convex).</summary>
            Convex = 4
        }

        // Neighbor offsets for each edge direction [direction][neighbor_idx][x,y]
        private static readonly int[][][] EdgeNeighborOffsets = new int[][][]
        {
            // Horizontal: left (-1,0) and right (1,0)
            new int[][] { new int[] { -1, 0 }, new int[] { 1, 0 } },
            // Vertical: top (0,-1) and bottom (0,1)
            new int[][] { new int[] { 0, -1 }, new int[] { 0, 1 } },
            // Diagonal 135: top-left (-1,-1) and bottom-right (1,1)
            new int[][] { new int[] { -1, -1 }, new int[] { 1, 1 } },
            // Diagonal 45: top-right (1,-1) and bottom-left (-1,1)
            new int[][] { new int[] { 1, -1 }, new int[] { -1, 1 } }
        };

        /// <summary>
        /// Applies SAO filtering to a CTU.
        /// </summary>
        /// <param name="samples">Reconstructed samples (modified in-place).</param>
        /// <param name="stride">Row stride in samples.</param>
        /// <param name="width">CTU width.</param>
        /// <param name="height">CTU height.</param>
        /// <param name="parameters">SAO parameters for this CTU.</param>
        /// <param name="bitDepth">Bit depth of samples.</param>
        public static void ApplySao(
            Span<byte> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth = 8)
        {
            if (parameters.IsOff)
                return;

            if (parameters.IsBandOffset)
            {
                ApplyBandOffset(samples, stride, width, height, parameters, bitDepth);
            }
            else if (parameters.IsEdgeOffset)
            {
                ApplyEdgeOffset(samples, stride, width, height, parameters, bitDepth);
            }
        }

        /// <summary>
        /// Applies SAO filtering to high bit-depth samples.
        /// </summary>
        public static void ApplySaoHighBitDepth(
            Span<ushort> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            if (parameters.IsOff)
                return;

            if (parameters.IsBandOffset)
            {
                ApplyBandOffsetHighBitDepth(samples, stride, width, height, parameters, bitDepth);
            }
            else if (parameters.IsEdgeOffset)
            {
                ApplyEdgeOffsetHighBitDepth(samples, stride, width, height, parameters, bitDepth);
            }
        }

        #region Band Offset

        /// <summary>
        /// Applies band offset SAO mode.
        /// Pixels are classified into 32 intensity bands, with offsets applied to 4 consecutive bands.
        /// </summary>
        private static void ApplyBandOffset(
            Span<byte> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int bandShift = bitDepth - 5;  // Divides pixel range into 32 bands

            // Precompute band offsets (only 4 consecutive bands have offsets)
            Span<int> bandOffsets = stackalloc int[32];
            int startBand = parameters.SaoBandPosition;
            for (int i = 0; i < 4 && i < parameters.SaoOffset.Length; i++)
            {
                int band = (startBand + i) & 31;  // Wrap around
                bandOffsets[band] = parameters.SaoOffset[i];
            }

            // Apply offsets
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    int value = samples[idx];
                    int band = value >> bandShift;
                    int offset = bandOffsets[band];
                    samples[idx] = (byte)Math.Clamp(value + offset, 0, maxValue);
                }
            }
        }

        private static void ApplyBandOffsetHighBitDepth(
            Span<ushort> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int bandShift = bitDepth - 5;

            Span<int> bandOffsets = stackalloc int[32];
            int startBand = parameters.SaoBandPosition;
            for (int i = 0; i < 4 && i < parameters.SaoOffset.Length; i++)
            {
                int band = (startBand + i) & 31;
                bandOffsets[band] = parameters.SaoOffset[i];
            }

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    int value = samples[idx];
                    int band = value >> bandShift;
                    int offset = bandOffsets[band];
                    samples[idx] = (ushort)Math.Clamp(value + offset, 0, maxValue);
                }
            }
        }

        #endregion

        #region Edge Offset

        /// <summary>
        /// Applies edge offset SAO mode.
        /// Pixels are classified by comparing to neighbors in the specified direction.
        /// </summary>
        private static void ApplyEdgeOffset(
            Span<byte> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            if (Sse2.IsSupported && width >= 16)
            {
                ApplyEdgeOffsetSse2(samples, stride, width, height, parameters, bitDepth);
                return;
            }

            ApplyEdgeOffsetScalar(samples, stride, width, height, parameters, bitDepth);
        }

        private static void ApplyEdgeOffsetScalar(
            Span<byte> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int direction = parameters.SaoEoClass;
            int[] neighbor0 = EdgeNeighborOffsets[direction][0];
            int[] neighbor1 = EdgeNeighborOffsets[direction][1];

            // Edge offsets are indexed by category (0-4)
            // Category 2 (flat) always has offset 0
            ReadOnlySpan<sbyte> offsets = parameters.SaoOffset;

            // Process interior pixels (skip edges based on direction)
            int startX = direction == 0 || direction == 2 ? 1 : (direction == 3 ? 0 : 0);
            int endX = direction == 0 || direction == 3 ? width - 1 : (direction == 2 ? width - 1 : width);
            int startY = direction >= 1 ? 1 : 0;
            int endY = direction >= 1 ? height - 1 : height;

            // Adjust bounds for diagonal directions
            if (direction == 2)  // 135 degree
            {
                startX = 1;
                endX = width - 1;
                startY = 1;
                endY = height - 1;
            }
            else if (direction == 3)  // 45 degree
            {
                startX = 1;
                endX = width - 1;
                startY = 1;
                endY = height - 1;
            }

            for (int y = startY; y < endY; y++)
            {
                int rowOffset = y * stride;
                for (int x = startX; x < endX; x++)
                {
                    int idx = rowOffset + x;
                    int current = samples[idx];

                    // Get neighbor values
                    int n0Idx = (y + neighbor0[1]) * stride + (x + neighbor0[0]);
                    int n1Idx = (y + neighbor1[1]) * stride + (x + neighbor1[0]);
                    int n0 = samples[n0Idx];
                    int n1 = samples[n1Idx];

                    // Classify edge
                    int category = ClassifyEdge(current, n0, n1);

                    // Get offset for this category
                    int offset = GetEdgeCategoryOffset(category, offsets);

                    // Apply offset
                    samples[idx] = (byte)Math.Clamp(current + offset, 0, maxValue);
                }
            }
        }

        private static unsafe void ApplyEdgeOffsetSse2(
            Span<byte> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            // For complex edge directions, fall back to scalar
            // SSE2 optimization would require careful boundary handling
            ApplyEdgeOffsetScalar(samples, stride, width, height, parameters, bitDepth);
        }

        private static void ApplyEdgeOffsetHighBitDepth(
            Span<ushort> samples,
            int stride,
            int width,
            int height,
            in HevcSaoParameters parameters,
            int bitDepth)
        {
            int maxValue = (1 << bitDepth) - 1;
            int direction = parameters.SaoEoClass;
            int[] neighbor0 = EdgeNeighborOffsets[direction][0];
            int[] neighbor1 = EdgeNeighborOffsets[direction][1];

            ReadOnlySpan<sbyte> offsets = parameters.SaoOffset;

            // Process interior pixels
            int startX = 1;
            int endX = width - 1;
            int startY = direction >= 1 ? 1 : 0;
            int endY = direction >= 1 ? height - 1 : height;

            for (int y = startY; y < endY; y++)
            {
                int rowOffset = y * stride;
                for (int x = startX; x < endX; x++)
                {
                    int idx = rowOffset + x;
                    int current = samples[idx];

                    int n0Idx = (y + neighbor0[1]) * stride + (x + neighbor0[0]);
                    int n1Idx = (y + neighbor1[1]) * stride + (x + neighbor1[0]);
                    int n0 = samples[n0Idx];
                    int n1 = samples[n1Idx];

                    int category = ClassifyEdge(current, n0, n1);
                    int offset = GetEdgeCategoryOffset(category, offsets);

                    samples[idx] = (ushort)Math.Clamp(current + offset, 0, maxValue);
                }
            }
        }

        /// <summary>
        /// Classifies a pixel into an edge category based on neighbor comparison.
        /// </summary>
        /// <param name="current">Current pixel value.</param>
        /// <param name="neighbor0">First neighbor value.</param>
        /// <param name="neighbor1">Second neighbor value.</param>
        /// <returns>Edge category (0-4).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClassifyEdge(int current, int neighbor0, int neighbor1)
        {
            // Sign function: -1 if a < b, 0 if a == b, 1 if a > b
            int sign0 = Math.Sign(current - neighbor0);
            int sign1 = Math.Sign(current - neighbor1);

            // Category is determined by the sum of signs
            // sum = -2: Concave (local minimum)
            // sum = -1: Valley
            // sum =  0: Flat
            // sum = +1: Peak
            // sum = +2: Convex (local maximum)
            int sum = sign0 + sign1;

            return sum + 2;  // Map to 0-4 range
        }

        /// <summary>
        /// Gets the offset for an edge category.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetEdgeCategoryOffset(int category, ReadOnlySpan<sbyte> offsets)
        {
            // Categories 0,1 use positive offsets (boost local minima)
            // Category 2 (flat) has no offset
            // Categories 3,4 use negative offsets (reduce local maxima)
            return category switch
            {
                0 => offsets.Length > 0 ? offsets[0] : 0,   // Concave
                1 => offsets.Length > 1 ? offsets[1] : 0,   // Valley
                2 => 0,                                      // Flat
                3 => offsets.Length > 2 ? -offsets[2] : 0,  // Peak (negative)
                4 => offsets.Length > 3 ? -offsets[3] : 0,  // Convex (negative)
                _ => 0
            };
        }

        #endregion

        #region CTU Processing

        /// <summary>
        /// Processes SAO for a complete frame, CTU by CTU.
        /// </summary>
        /// <param name="lumaPlane">Luma plane samples.</param>
        /// <param name="cbPlane">Cb chroma plane samples.</param>
        /// <param name="crPlane">Cr chroma plane samples.</param>
        /// <param name="lumaStride">Luma plane stride.</param>
        /// <param name="chromaStride">Chroma plane stride.</param>
        /// <param name="frameWidth">Frame width in luma samples.</param>
        /// <param name="frameHeight">Frame height in luma samples.</param>
        /// <param name="ctuSize">CTU size in luma samples.</param>
        /// <param name="getSaoParams">Function to get SAO parameters for each CTU and component.</param>
        /// <param name="bitDepth">Bit depth.</param>
        public static void ProcessFrame(
            Span<byte> lumaPlane,
            Span<byte> cbPlane,
            Span<byte> crPlane,
            int lumaStride,
            int chromaStride,
            int frameWidth,
            int frameHeight,
            int ctuSize,
            Func<int, int, int, HevcSaoParameters> getSaoParams,
            int bitDepth = 8)
        {
            int ctuWidthCount = (frameWidth + ctuSize - 1) / ctuSize;
            int ctuHeightCount = (frameHeight + ctuSize - 1) / ctuSize;
            int chromaCtuSize = ctuSize / 2;  // Assuming 4:2:0

            for (int ctuY = 0; ctuY < ctuHeightCount; ctuY++)
            {
                for (int ctuX = 0; ctuX < ctuWidthCount; ctuX++)
                {
                    // Calculate CTU position and size (handling frame edges)
                    int lumaX = ctuX * ctuSize;
                    int lumaY = ctuY * ctuSize;
                    int actualWidth = Math.Min(ctuSize, frameWidth - lumaX);
                    int actualHeight = Math.Min(ctuSize, frameHeight - lumaY);

                    // Process luma
                    var lumaParams = getSaoParams(ctuX, ctuY, 0);
                    if (!lumaParams.IsOff)
                    {
                        var ctuLuma = lumaPlane.Slice(lumaY * lumaStride + lumaX);
                        ApplySao(ctuLuma, lumaStride, actualWidth, actualHeight, lumaParams, bitDepth);
                    }

                    // Process Cb
                    int chromaX = ctuX * chromaCtuSize;
                    int chromaY = ctuY * chromaCtuSize;
                    int chromaWidth = actualWidth / 2;
                    int chromaHeight = actualHeight / 2;

                    var cbParams = getSaoParams(ctuX, ctuY, 1);
                    if (!cbParams.IsOff)
                    {
                        var ctuCb = cbPlane.Slice(chromaY * chromaStride + chromaX);
                        ApplySao(ctuCb, chromaStride, chromaWidth, chromaHeight, cbParams, bitDepth);
                    }

                    // Process Cr
                    var crParams = getSaoParams(ctuX, ctuY, 2);
                    if (!crParams.IsOff)
                    {
                        var ctuCr = crPlane.Slice(chromaY * chromaStride + chromaX);
                        ApplySao(ctuCr, chromaStride, chromaWidth, chromaHeight, crParams, bitDepth);
                    }
                }
            }
        }

        #endregion

        #region SAO Merge

        /// <summary>
        /// Merges SAO parameters from a neighboring CTU.
        /// Used when sao_merge_left_flag or sao_merge_up_flag is set.
        /// </summary>
        /// <param name="target">Target parameters to update.</param>
        /// <param name="source">Source parameters to copy from.</param>
        public static void MergeParameters(ref HevcSaoParameters target, in HevcSaoParameters source)
        {
            target.SaoTypeIdx = source.SaoTypeIdx;
            target.SaoBandPosition = source.SaoBandPosition;
            target.SaoEoClass = source.SaoEoClass;

            // Copy offsets
            if (source.SaoOffset.Length > 0)
            {
                target.SaoOffset = new sbyte[source.SaoOffset.Length];
                source.SaoOffset.CopyTo(target.SaoOffset, 0);
            }
        }

        #endregion
    }

    /// <summary>
    /// SAO parameters for a CTU component (luma or chroma).
    /// </summary>
    /// <remarks>
    /// Already defined in HevcCodingTree.cs, but included here for reference.
    /// </remarks>
}
