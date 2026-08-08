// HEVC Intra Prediction and Reconstruction
// Reference: ITU-T H.265 Section 8.4.4, FFmpeg hevc/pred_intra.c

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc;

internal sealed partial class HevcDecoder
{
    // HEVC intra prediction modes
    private const int IntraPlanar = 0;
    private const int IntraDc = 1;
    // Angular modes: 2-34 (2=horizontal-ish, 26=vertical, 34=diagonal)

    // Angular mode displacement table (Table 8-4 in spec)
    // Index by (intraPredMode - 2): gives the displacement value
    private static readonly int[] IntraAngleTable =
    {
        32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26, -32,
        -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32
    };

    // Inverse angle table for reference sample projection
    private static readonly int[] InverseAngleTable =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        4096, 1638, 910, 630, 482, 390, 315, 256,
        315, 390, 482, 630, 910, 1638, 4096, 0,
        0, 0, 0, 0, 0, 0, 0
    };

    /// <summary>
    /// Performs intra prediction for a block.
    /// cIdx: 0=luma, 1=Cb, 2=Cr
    /// </summary>
    private void PerformIntraPrediction(HevcSequenceParameterSet sps,
        bool constrainedIntraPred, int x0, int y0, int log2TrafoSize, int intraPredMode, int cIdx)
    {
        if (currentFrameBuffer == null) return;

        DiagIntraPredLog?.Add((x0, y0, 1 << log2TrafoSize, intraPredMode, cIdx));

        int trafoSize = 1 << log2TrafoSize;
        int bitDepth = (cIdx == 0) ? (sps.BitDepthLumaMinus8 + 8) : (sps.BitDepthChromaMinus8 + 8);
        int maxVal = (1 << bitDepth) - 1;

        // Get plane parameters
        GetPlaneParams(sps, cIdx, out int planeOffset, out int stride, out int planeWidth, out int planeHeight);

        // Adjust coordinates for chroma subsampling
        int xP = (cIdx > 0) ? x0 >> sps.HShiftChroma : x0;
        int yP = (cIdx > 0) ? y0 >> sps.VShiftChroma : y0;

        // Get reference samples
        int refSize = 2 * trafoSize + 1;
        Span<int> refAbove = stackalloc int[refSize + 1]; // p[-1..2*nTbS-1] (above row + corner)
        Span<int> refLeft = stackalloc int[refSize + 1];  // p[-1..2*nTbS-1] (left column + corner)

        FillReferenceSamples(sps, constrainedIntraPred, xP, yP, trafoSize, cIdx, bitDepth, planeOffset, stride,
            planeWidth, planeHeight, refAbove, refLeft);


        // Reference sample filtering (smoothing) — matches FFmpeg pred_template.c
        // Applied for non-DC modes, size >= 8, when not intra_smoothing_disabled
        // Also applies strong intra smoothing for 32x32 luma blocks
        if (!sps.IntraSmoothingDisabled && intraPredMode != IntraDc && trafoSize >= 8 &&
            (cIdx == 0 || sps.ChromaFormatIdc == HevcChromaFormat.Chroma444))
        {
            // Check if mode is far enough from horizontal/vertical to need filtering
            // intra_hor_ver_dist_thresh[log2Size-3]: {7, 1, 0} for sizes {8, 16, 32}
            int minDistVertHor = Math.Min(
                Math.Abs(intraPredMode - 26), // distance from vertical
                Math.Abs(intraPredMode - 10)); // distance from horizontal

            int threshIdx = log2TrafoSize - 3; // 0 for 8x8, 1 for 16x16, 2 for 32x32
            ReadOnlySpan<int> distThresh = [7, 1, 0];
            if (threshIdx >= 0 && threshIdx < 3 && minDistVertHor > distThresh[threshIdx])
            {
                int threshold = 1 << (bitDepth - 5);
                if (sps.StrongIntraSmoothingEnabled && cIdx == 0 &&
                    log2TrafoSize == 5 &&
                    Math.Abs(refAbove[0] + refAbove[2 * trafoSize] - 2 * refAbove[trafoSize]) < threshold &&
                    Math.Abs(refLeft[0] + refLeft[2 * trafoSize] - 2 * refLeft[trafoSize]) < threshold)
                {
                    // Strong intra smoothing: bilinear interpolation for 32x32 luma
                    // Apply to refAbove in-place (corner stays, endpoint stays, interpolate between)
                    int topCorner = refAbove[0]; // top[-1]
                    int topEnd = refAbove[2 * trafoSize]; // top[63]
                    for (int i = 1; i < 2 * trafoSize; i++)
                        refAbove[i] = ((2 * trafoSize - i) * topCorner + i * topEnd + trafoSize) >> (log2TrafoSize + 1);

                    // Apply to refLeft in-place (corner stays, endpoint stays)
                    int leftCorner = refLeft[0]; // left[-1]
                    int leftEnd = refLeft[2 * trafoSize]; // left[63]
                    for (int i = 1; i < 2 * trafoSize; i++)
                        refLeft[i] = ((2 * trafoSize - i) * leftCorner + i * leftEnd + trafoSize) >> (log2TrafoSize + 1);
                }
                else
                {
                    // Standard 3-tap smoothing filter [1, 2, 1] / 4
                    // Filter left array (in-place from end to start to avoid overwrites)
                    Span<int> filtLeft = stackalloc int[2 * trafoSize + 1];
                    filtLeft[2 * trafoSize] = refLeft[2 * trafoSize];
                    for (int i = 2 * trafoSize - 1; i >= 1; i--)
                        filtLeft[i] = (refLeft[i - 1] + 2 * refLeft[i] + refLeft[i + 1] + 2) >> 2;
                    filtLeft[0] = (refLeft[1] + 2 * refLeft[0] + refAbove[1] + 2) >> 2;

                    // Filter above array
                    Span<int> filtAbove = stackalloc int[2 * trafoSize + 1];
                    filtAbove[2 * trafoSize] = refAbove[2 * trafoSize];
                    for (int i = 2 * trafoSize - 1; i >= 1; i--)
                        filtAbove[i] = (refAbove[i - 1] + 2 * refAbove[i] + refAbove[i + 1] + 2) >> 2;
                    filtAbove[0] = filtLeft[0]; // corner shared

                    // Copy filtered back
                    filtLeft.CopyTo(refLeft);
                    filtAbove.CopyTo(refAbove);
                }
            }
        }

        // Perform prediction based on mode
        var buffer = currentFrameBuffer.AsSpan();
        int bytesPerSample = bitDepth > 8 ? 2 : 1;

        if (intraPredMode == IntraPlanar)
            PredictPlanar(buffer, planeOffset, stride, xP, yP, trafoSize, bitDepth, refAbove, refLeft);
        else if (intraPredMode == IntraDc)
            PredictDc(buffer, planeOffset, stride, xP, yP, trafoSize, bitDepth, cIdx, refAbove, refLeft);
        else
            PredictAngular(buffer, planeOffset, stride, xP, yP, trafoSize, bitDepth, intraPredMode, cIdx, refAbove, refLeft);
    }

    /// <summary>
    /// Gets plane memory parameters for the current frame buffer.
    /// </summary>
    private void GetPlaneParams(HevcSequenceParameterSet sps, int cIdx,
        out int planeOffset, out int stride, out int planeWidth, out int planeHeight)
    {
        int width = sps.PictureWidthInLumaSamples;
        int height = sps.PictureHeightInLumaSamples;
        int bitDepth = (cIdx == 0) ? (sps.BitDepthLumaMinus8 + 8) : (sps.BitDepthChromaMinus8 + 8);
        int bytesPerSample = bitDepth > 8 ? 2 : 1;

        if (cIdx == 0)
        {
            planeOffset = 0;
            stride = width * bytesPerSample;
            planeWidth = width;
            planeHeight = height;
        }
        else
        {
            int hShift = sps.HShiftChroma;
            int vShift = sps.VShiftChroma;
            int chromaWidth = width >> hShift;
            int chromaHeight = height >> vShift;
            int lumaPlaneSize = width * height * bytesPerSample;
            int chromaPlaneSize = chromaWidth * chromaHeight * bytesPerSample;

            planeOffset = lumaPlaneSize + (cIdx == 2 ? chromaPlaneSize : 0);
            stride = chromaWidth * bytesPerSample;
            planeWidth = chromaWidth;
            planeHeight = chromaHeight;
        }
    }

    /// <summary>
    /// Fills reference samples from the reconstructed frame buffer with proper
    /// unavailable sample substitution. Matches FFmpeg's FUNC(intra_pred) in pred_template.c.
    /// 
    /// Layout: refAbove[0] = p[-1,-1] (corner), refAbove[1..2*nTbS] = p[0..2*nTbS-1, -1]
    ///         refLeft[0]  = p[-1,-1] (corner), refLeft[1..2*nTbS]  = p[-1, 0..2*nTbS-1]
    /// </summary>
    private void FillReferenceSamples(HevcSequenceParameterSet sps,
        bool constrainedIntraPred,
        int xP, int yP, int trafoSize, int cIdx, int bitDepth,
        int planeOffset, int stride, int planeWidth, int planeHeight,
        Span<int> refAbove, Span<int> refLeft)
    {
        if (currentFrameBuffer == null) return;

        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        var buffer = currentFrameBuffer.AsSpan();
        int dcVal = 1 << (bitDepth - 1);

        // Convert plane coordinates back to luma coordinates for availability calculation
        // FFmpeg pred_template.c: separate H/V sizes for non-square chroma in 422
        int hShift = (cIdx > 0) ? sps.HShiftChroma : 0;
        int vShift = (cIdx > 0) ? sps.VShiftChroma : 0;
        int lumaX = xP << hShift;
        int lumaY = yP << vShift;
        int lumaTfSizeH = trafoSize << hShift;
        int lumaTfSizeV = trafoSize << vShift;

        // Z-scan address computation for sub-CTU TU availability
        // Matches FFmpeg's min_tb_addr_zs table and pred_template.c availability checks
        int ctbSize = sps.CtbSizeY;
        int picWidthInCtbs = sps.PicWidthInCtbsY;
        int log2MinTbSize = sps.Log2MinTbSizeY;
        int log2CtbSize = sps.Log2CtbSizeY;
        int log2Diff = log2CtbSize - log2MinTbSize;
        int tbMask = (1 << log2Diff) - 1;

        // Current TU z-scan address (luma coords, masked to within-CTU)
        int xTb = (lumaX >> log2MinTbSize) & tbMask;
        int yTb = (lumaY >> log2MinTbSize) & tbMask;
        int curCtbAddr = (lumaY / ctbSize) * picWidthInCtbs + (lumaX / ctbSize);
        int curZsAddr = (curCtbAddr << (log2Diff * 2)) + ComputeZScanAddr(xTb, yTb, log2Diff);

        // Size in min-TB units (in luma) — separate H and V for 422
        int sizeInTbsH = lumaTfSizeH >> log2MinTbSize;
        int sizeInTbsV = lumaTfSizeV >> log2MinTbSize;

        // FFmpeg pred_template.c:89 — spin handles 422 chroma blocks smaller than min TB
        int spin = (cIdx > 0 && sizeInTbsV == 0 && ((2 * lumaY) & (1 << log2MinTbSize)) != 0) ? 1 : 0;

        // CTU-level neighbor availability (matches FFmpeg's ff_hevc_set_neighbour_available)
        // At CTB boundaries, cross-slice/tile neighbors are blocked by ctb*Flag.
        // Within a CTB (x0b > 0 or y0b > 0), neighbors are always available.
        int x0b = lumaX & (ctbSize - 1); // position within CTB
        int y0b = lumaY & (ctbSize - 1);
        bool candLeft = ctbLeftFlag || x0b > 0;
        bool candUp = ctbUpFlag || y0b > 0;
        bool candUpLeft = (x0b > 0 || y0b > 0) ? (candLeft && candUp) : ctbUpLeftFlag;

        // Up-right: match FFmpeg's cand_up_right_sap logic
        // If up-right is in a different CTB (x0b + blockSize reaches CTB edge): use ctbUpRightFlag && at top of CTB
        // Otherwise: same as candUp, plus z-scan order check
        // FFmpeg pred_template.c:114: cand_up_right also requires !spin
        bool candUpRight = false;
        {
            bool candUpRightSap;
            if (x0b + lumaTfSizeH >= ctbSize)
                candUpRightSap = ctbUpRightFlag && y0b == 0;
            else
                candUpRightSap = candUp;
            
            if (candUpRightSap && spin == 0 && (xP + trafoSize) < planeWidth)
            {
                int urLumaX = lumaX + lumaTfSizeH;
                int urLumaY = lumaY - 1;
                if (urLumaY >= 0)
                {
                    int urCtbAddr = (urLumaY / ctbSize) * picWidthInCtbs + (urLumaX / ctbSize);
                    if (urCtbAddr < curCtbAddr)
                    {
                        candUpRight = true;
                    }
                    else if (urCtbAddr == curCtbAddr)
                    {
                        int urXTb = (xTb + sizeInTbsH) & tbMask;
                        int urYTb = (yTb - 1) & tbMask;
                        int urZsLocal = ComputeZScanAddr(urXTb, urYTb, log2Diff);
                        int curZsLocal = ComputeZScanAddr(xTb, yTb, log2Diff);
                        candUpRight = curZsLocal > urZsLocal;
                    }
                }
            }
        }

        // Bottom-left: match FFmpeg's logic
        // If bottom-left goes beyond current CTB row bottom: not available
        // Otherwise: same as candLeft, plus z-scan order check
        int endOfTilesY = Math.Min(((lumaY / ctbSize) + 1) * ctbSize, sps.PictureHeightInLumaSamples);
        int endOfTilesYPlane = (cIdx > 0) ? endOfTilesY >> sps.VShiftChroma : endOfTilesY;
        bool candBottomLeft = false;
        if (candLeft && (yP + trafoSize) < endOfTilesYPlane)
        {
            int blLumaX = lumaX - 1;
            int blLumaY = lumaY + lumaTfSizeV;
            if (blLumaX >= 0 && blLumaY < sps.PictureHeightInLumaSamples)
            {
                int blCtbAddr = (blLumaY / ctbSize) * picWidthInCtbs + (blLumaX / ctbSize);
                if (blCtbAddr < curCtbAddr)
                {
                    candBottomLeft = true;
                }
                else if (blCtbAddr == curCtbAddr)
                {
                    int blXTb = (xTb - 1) & tbMask;
                    int blYTb = (yTb + sizeInTbsV + spin) & tbMask;
                    int blZsLocal = ComputeZScanAddr(blXTb, blYTb, log2Diff);
                    int curZsLocal = ComputeZScanAddr(xTb, yTb, log2Diff);
                    candBottomLeft = curZsLocal > blZsLocal;
                }
            }
        }

        // Clamp bottom-left/up-right size to picture boundary
        int bottomLeftSize = candBottomLeft
            ? Math.Min(yP + 2 * trafoSize, planeHeight) - (yP + trafoSize)
            : 0;
        int topRightSize = candUpRight
            ? Math.Min(xP + 2 * trafoSize, planeWidth) - (xP + trafoSize)
            : 0;

        // CIP coarse refinement: when constrained_intra_pred_flag is set,
        // mark neighbor regions as unavailable if ALL their PU blocks are inter-coded.
        // Matches FFmpeg pred_template.c lines 121-168.
        if (constrainedIntraPred && predModeField != null)
        {
            int puMask = (1 << 2) - 1; // log2_min_pu_size = 2
            bool onPuEdgeX = (lumaX & puMask) == 0;
            bool onPuEdgeY = (lumaY & puMask) == 0;
            int sizeInLumaPuV = lumaTfSizeV >> 2;
            int sizeInLumaPuH = lumaTfSizeH >> 2;
            if (sizeInLumaPuH == 0) sizeInLumaPuH = 1;

            if (candBottomLeft && onPuEdgeX)
            {
                int xLeftPu = (lumaX - 1) >> 2;
                int yBottomPu = (lumaY + lumaTfSizeV) >> 2;
                int max = Math.Min(sizeInLumaPuV, puHeightIn4 - yBottomPu);
                candBottomLeft = false;
                for (int i = 0; i < max; i += 2)
                    candBottomLeft |= predModeField[(yBottomPu + i) * puWidthIn4 + xLeftPu] == 0;
            }

            if (candLeft && onPuEdgeX)
            {
                int xLeftPu = (lumaX - 1) >> 2;
                int yLeftPu = lumaY >> 2;
                int max = Math.Min(sizeInLumaPuV, puHeightIn4 - yLeftPu);
                candLeft = false;
                for (int i = 0; i < max; i += 2)
                    candLeft |= predModeField[(yLeftPu + i) * puWidthIn4 + xLeftPu] == 0;
            }

            if (candUpLeft)
            {
                int xLeftPu = (lumaX - 1) >> 2;
                int yTopPu = (lumaY - 1) >> 2;
                candUpLeft = predModeField[yTopPu * puWidthIn4 + xLeftPu] == 0;
            }

            if (candUp && onPuEdgeY)
            {
                int xTopPu = lumaX >> 2;
                int yTopPu = (lumaY - 1) >> 2;
                int max = Math.Min(sizeInLumaPuH, puWidthIn4 - xTopPu);
                candUp = false;
                for (int i = 0; i < max; i += 2)
                    candUp |= predModeField[yTopPu * puWidthIn4 + (xTopPu + i)] == 0;
            }

            if (candUpRight && onPuEdgeY)
            {
                int yTopPu = (lumaY - 1) >> 2;
                int xRightPu = (lumaX + lumaTfSizeH) >> 2;
                int max = Math.Min(sizeInLumaPuH, puWidthIn4 - xRightPu);
                candUpRight = false;
                for (int i = 0; i < max; i += 2)
                    candUpRight |= predModeField[yTopPu * puWidthIn4 + (xRightPu + i)] == 0;
            }

            // Initialize all reference samples to DC value for CIP
            for (int i = 0; i <= 2 * trafoSize; i++)
            {
                refAbove[i] = dcVal;
                refLeft[i] = dcVal;
            }
        }
        else
        {
            // Initialize all to 0 (will be overwritten by filling and substitution)
            refAbove.Clear();
            refLeft.Clear();
        }

        // Fill available samples
        if (candUpLeft)
        {
            int val = ReadSample(buffer, planeOffset, stride, xP - 1, yP - 1, bytesPerSample);
            refAbove[0] = val; // top-left corner
            refLeft[0] = val;
        }

        if (candUp)
        {
            int count = Math.Min(trafoSize, planeWidth - xP);
            for (int x = 0; x < count; x++)
                refAbove[x + 1] = ReadSample(buffer, planeOffset, stride, xP + x, yP - 1, bytesPerSample);
        }

        if (candUpRight)
        {
            for (int x = 0; x < topRightSize; x++)
                refAbove[trafoSize + x + 1] = ReadSample(buffer, planeOffset, stride, xP + trafoSize + x, yP - 1, bytesPerSample);
            // Extend right edge if top-right region is smaller than nTbS
            int extVal = refAbove[trafoSize + topRightSize];
            for (int x = topRightSize; x < trafoSize; x++)
                refAbove[trafoSize + x + 1] = extVal;
        }

        if (candLeft)
        {
            int count = Math.Min(trafoSize, planeHeight - yP);
            for (int y = 0; y < count; y++)
                refLeft[y + 1] = ReadSample(buffer, planeOffset, stride, xP - 1, yP + y, bytesPerSample);
        }

        if (candBottomLeft)
        {
            for (int y = 0; y < bottomLeftSize; y++)
                refLeft[trafoSize + y + 1] = ReadSample(buffer, planeOffset, stride, xP - 1, yP + trafoSize + y, bytesPerSample);
            // Extend bottom edge if bottom-left region is smaller than nTbS
            int extVal = refLeft[trafoSize + bottomLeftSize];
            for (int y = bottomLeftSize; y < trafoSize; y++)
                refLeft[trafoSize + y + 1] = extVal;
        }

        // CIP fine-grained extension: overwrite inter-sourced samples with extended intra values.
        // Matches FFmpeg pred_template.c lines 190-251.
        if (constrainedIntraPred && predModeField != null &&
            (candBottomLeft || candLeft || candUpLeft || candUp || candUpRight))
        {
            // Compute max extent in plane coordinates
            int sizeMaxX = (xP + 2 * trafoSize < planeWidth)
                ? 2 * trafoSize : (planeWidth - xP);
            int sizeMaxY = (yP + 2 * trafoSize < planeHeight)
                ? 2 * trafoSize : (planeHeight - yP);

            int j = trafoSize + (candBottomLeft ? bottomLeftSize : 0) - 1;

            if (!candUpRight)
                sizeMaxX = (xP + trafoSize < planeWidth) ? trafoSize : (planeWidth - xP);
            if (!candBottomLeft)
                sizeMaxY = (yP + trafoSize < planeHeight) ? trafoSize : (planeHeight - yP);

            // --- Find initial seed sample ---
            // Scan left column bottom-to-top for first intra block, then top row if needed
            if (candBottomLeft || candLeft || candUpLeft)
            {
                // Scan left column from bottom to find an intra sample
                while (j > -1 && !CipIsIntra(lumaX, lumaY, hShift, vShift, -1, j))
                    j--;
                if (j < -1 || !CipIsIntra(lumaX, lumaY, hShift, vShift, -1, j))
                {
                    // No intra in left column; search top row left-to-right
                    j = 0;
                    while (j < sizeMaxX && !CipIsIntra(lumaX, lumaY, hShift, vShift, j, -1))
                        j++;
                    // EXTEND_LEFT_CIP(top, j, j + 1): propagate rightward to leftward
                    for (int i = j; i > -1; i--)
                    {
                        if (!CipIsIntra(lumaX, lumaY, hShift, vShift, i - 1, -1))
                            refAbove[i] = refAbove[i + 1]; // top[i-1] = top[i]
                    }
                    refLeft[0] = refAbove[0]; // left[-1] = top[-1]
                }
            }
            else
            {
                // No left/bottom-left/up-left — scan top row for first intra
                j = 0;
                while (j < sizeMaxX && !CipIsIntra(lumaX, lumaY, hShift, vShift, j, -1))
                    j++;
                if (j > 0)
                {
                    // EXTEND_LEFT_CIP(top, j, j)
                    for (int i = j; i > 0; i--)
                    {
                        if (!CipIsIntra(lumaX, lumaY, hShift, vShift, i - 1, -1))
                            refAbove[i] = refAbove[i + 1];
                    }
                    refAbove[0] = refAbove[1]; // top[-1] = top[0]
                }
                refLeft[0] = refAbove[0]; // left[-1] = top[-1]
            }
            refLeft[0] = refAbove[0]; // left[-1] = top[-1]

            // --- Extend left column downward with CIP ---
            if (candBottomLeft || candLeft)
            {
                int seedVal = refLeft[0]; // a = left[-1]
                // EXTEND_DOWN_CIP(left, 0, sizeMaxY)
                for (int i = 0; i < sizeMaxY; i += 4)
                {
                    if (!CipIsIntra(lumaX, lumaY, hShift, vShift, -1, i))
                    {
                        int end = Math.Min(i + 4, sizeMaxY);
                        for (int k = i; k < end; k++)
                            refLeft[k + 1] = seedVal;
                    }
                    else
                    {
                        int lastIdx = Math.Min(i + 3, sizeMaxY - 1);
                        seedVal = refLeft[lastIdx + 1];
                    }
                }
            }
            if (!candLeft)
            {
                int val = refLeft[0]; // left[-1]
                for (int i = 0; i < trafoSize; i++)
                    refLeft[i + 1] = val;
            }
            if (!candBottomLeft)
            {
                int val = refLeft[trafoSize]; // left[size-1]
                for (int i = 0; i < trafoSize; i++)
                    refLeft[trafoSize + i + 1] = val;
            }

            // --- Extend left column upward with CIP ---
            if (lumaX != 0 && lumaY != 0)
            {
                int seedVal = refLeft[sizeMaxY]; // a = left[sizeMaxY - 1]
                // EXTEND_UP_CIP(left, sizeMaxY - 1, sizeMaxY)
                for (int i = sizeMaxY - 1; i > sizeMaxY - 1 - sizeMaxY; i -= 4)
                {
                    int startIdx = i - 3;
                    if (!CipIsIntra(lumaX, lumaY, hShift, vShift, -1, startIdx))
                    {
                        for (int k = startIdx; k <= i; k++)
                            if (k >= 0) refLeft[k + 1] = seedVal;
                    }
                    else
                    {
                        if (startIdx >= 0)
                            seedVal = refLeft[startIdx + 1];
                    }
                }
                if (!CipIsIntra(lumaX, lumaY, hShift, vShift, -1, -1))
                    refLeft[0] = refLeft[1]; // left[-1] = left[0]
            }
            else if (lumaX == 0)
            {
                // At left picture edge, fill left with 0 (matches FFmpeg)
                for (int i = 0; i < sizeMaxY; i++)
                    refLeft[i + 1] = 0;
            }
            else
            {
                int seedVal = refLeft[sizeMaxY]; // a = left[sizeMaxY - 1]
                // EXTEND_UP_CIP(left, sizeMaxY - 1, sizeMaxY)
                for (int i = sizeMaxY - 1; i > sizeMaxY - 1 - sizeMaxY; i -= 4)
                {
                    int startIdx = i - 3;
                    if (!CipIsIntra(lumaX, lumaY, hShift, vShift, -1, startIdx))
                    {
                        for (int k = startIdx; k <= i; k++)
                            if (k >= 0) refLeft[k + 1] = seedVal;
                    }
                    else
                    {
                        if (startIdx >= 0)
                            seedVal = refLeft[startIdx + 1];
                    }
                }
            }

            // --- Set corner and extend top rightward ---
            refAbove[0] = refLeft[0]; // top[-1] = left[-1]
            if (lumaY != 0)
            {
                int seedVal = refLeft[0]; // a = left[-1]
                // EXTEND_RIGHT_CIP(top, 0, sizeMaxX)
                for (int i = 0; i < sizeMaxX; i += 4)
                {
                    if (!CipIsIntra(lumaX, lumaY, hShift, vShift, i, -1))
                    {
                        int end = Math.Min(i + 4, sizeMaxX);
                        for (int k = i; k < end; k++)
                            refAbove[k + 1] = seedVal;
                    }
                    else
                    {
                        int lastIdx = Math.Min(i + 3, sizeMaxX - 1);
                        seedVal = refAbove[lastIdx + 1];
                    }
                }
            }
        }

        // Reference sample substitution for unavailable neighbors
        // Matches FFmpeg's cascade in pred_template.c
        if (!candBottomLeft)
        {
            if (candLeft)
            {
                // Extend left downward: fill bottom-left from last available left sample
                int extVal = refLeft[trafoSize]; // left[size-1] in FFmpeg indexing
                for (int y = 0; y < trafoSize; y++)
                    refLeft[trafoSize + y + 1] = extVal;
            }
            else if (candUpLeft)
            {
                // Fill all left from corner
                int val = refLeft[0]; // left[-1]
                for (int i = 1; i <= 2 * trafoSize; i++)
                    refLeft[i] = val;
                candLeft = true;
            }
            else if (candUp)
            {
                // Copy first top sample to corner, fill all left
                refLeft[0] = refAbove[1]; // left[-1] = top[0]
                refAbove[0] = refAbove[1];
                int val = refLeft[0];
                for (int i = 1; i <= 2 * trafoSize; i++)
                    refLeft[i] = val;
                candUpLeft = true;
                candLeft = true;
            }
            else if (candUpRight)
            {
                // Fill top from top-right, copy to corner, fill all left
                int val = refAbove[trafoSize + 1]; // top[size] in FFmpeg
                for (int x = 0; x < trafoSize; x++)
                    refAbove[x + 1] = val;
                refAbove[0] = val;
                refLeft[0] = val;
                for (int i = 1; i <= 2 * trafoSize; i++)
                    refLeft[i] = val;
                candUp = true;
                candUpLeft = true;
                candLeft = true;
            }
            else
            {
                // No samples available at all — use DC
                refAbove[0] = dcVal;
                refLeft[0] = dcVal;
                for (int i = 1; i <= 2 * trafoSize; i++)
                {
                    refAbove[i] = dcVal;
                    refLeft[i] = dcVal;
                }
                return; // Nothing more to substitute
            }
        }

        if (!candLeft)
        {
            // Fill left from bottom-left (which must be available by now)
            int val = refLeft[trafoSize + 1]; // left[size] in FFmpeg
            for (int y = 0; y < trafoSize; y++)
                refLeft[y + 1] = val;
        }

        if (!candUpLeft)
        {
            // Corner = first left sample
            refLeft[0] = refLeft[1]; // left[-1] = left[0]
            refAbove[0] = refLeft[0];
        }

        if (!candUp)
        {
            // Fill top from corner
            int val = refLeft[0]; // left[-1]
            for (int x = 0; x < trafoSize; x++)
                refAbove[x + 1] = val;
        }

        if (!candUpRight)
        {
            // Extend top rightward from last available top sample
            int val = refAbove[trafoSize]; // top[size-1]
            for (int x = 0; x < trafoSize; x++)
                refAbove[trafoSize + x + 1] = val;
        }

        // Ensure corner consistency
        refAbove[0] = refLeft[0];
    }

    /// <summary>
    /// Checks if a neighbor block is intra-coded for constrained intra prediction.
    /// relX, relY are in plane sample coordinates relative to the current TU.
    /// Matches FFmpeg's IS_INTRA(x, y) macro in pred_template.c.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CipIsIntra(int tuLumaX, int tuLumaY, int hShift, int vShift, int relX, int relY)
    {
        int lx = tuLumaX + relX * (1 << hShift);
        int ly = tuLumaY + relY * (1 << vShift);
        int px = lx >> 2;
        int py = ly >> 2;
        if ((uint)px >= (uint)puWidthIn4 || (uint)py >= (uint)puHeightIn4)
            return false;
        return predModeField![py * puWidthIn4 + px] == 0;
    }

    /// <summary>
    /// Reads a sample value from the frame buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSample(Span<byte> buffer, int planeOffset, int stride,
        int x, int y, int bytesPerSample)
    {
        int offset = planeOffset + y * stride + x * bytesPerSample;
        if (offset < 0 || offset + bytesPerSample > buffer.Length)
            return 0;

        if (bytesPerSample == 1)
            return buffer[offset];
        else
            return buffer[offset] | (buffer[offset + 1] << 8);
    }

    /// <summary>
    /// Writes a sample value to the frame buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSample(Span<byte> buffer, int planeOffset, int stride,
        int x, int y, int bytesPerSample, int value)
    {
        int offset = planeOffset + y * stride + x * bytesPerSample;
        if (offset < 0 || offset + bytesPerSample > buffer.Length)
            return;

        if (bytesPerSample == 1)
            buffer[offset] = (byte)value;
        else
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// Computes the z-scan address for a min-TB position within a CTU.
    /// Matches FFmpeg's min_tb_addr_zs table computation in setup_pps().
    /// The z-scan follows the recursive quadtree order: TL(0), TR(1), BL(2), BR(3).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeZScanAddr(int xTb, int yTb, int log2Diff)
    {
        int val = 0;
        for (int i = 0; i < log2Diff; i++)
        {
            int m = 1 << i;
            if ((m & xTb) != 0) val += m * m;
            if ((m & yTb) != 0) val += 2 * m * m;
        }
        return val;
    }

    /// <summary>
    /// Planar intra prediction (mode 0).
    /// </summary>
    private static void PredictPlanar(Span<byte> buffer, int planeOffset, int stride,
        int xP, int yP, int nTbS, int bitDepth, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int log2Size = 0;
        while ((1 << log2Size) < nTbS) log2Size++;

        for (int y = 0; y < nTbS; y++)
        {
            for (int x = 0; x < nTbS; x++)
            {
                int predV = (nTbS - 1 - x) * refLeft[y + 1] + (x + 1) * refAbove[nTbS + 1];
                int predH = (nTbS - 1 - y) * refAbove[x + 1] + (y + 1) * refLeft[nTbS + 1];
                int val = (predV + predH + nTbS) >> (log2Size + 1);

                WriteSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample, val);
            }
        }
    }

    /// <summary>
    /// DC intra prediction (mode 1).
    /// </summary>
    private static void PredictDc(Span<byte> buffer, int planeOffset, int stride,
        int xP, int yP, int nTbS, int bitDepth, int cIdx, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int log2Size = 0;
        while ((1 << log2Size) < nTbS) log2Size++;

        // Compute DC value
        int dcSum = 0;
        for (int i = 0; i < nTbS; i++)
        {
            dcSum += refAbove[i + 1];
            dcSum += refLeft[i + 1];
        }
        int dcVal = (dcSum + nTbS) >> (log2Size + 1);

        // Fill block with DC value
        for (int y = 0; y < nTbS; y++)
            for (int x = 0; x < nTbS; x++)
                WriteSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample, dcVal);

        // DC filtering for luma only (HEVC spec 8.4.4.2.6, ffmpeg pred_template.c)
        if (cIdx == 0 && nTbS < 32)
        {
            // Top row: filtered
            WriteSample(buffer, planeOffset, stride, xP, yP, bytesPerSample,
                (refLeft[1] + 2 * dcVal + refAbove[1] + 2) >> 2);
            for (int x = 1; x < nTbS; x++)
                WriteSample(buffer, planeOffset, stride, xP + x, yP, bytesPerSample,
                    (refAbove[x + 1] + 3 * dcVal + 2) >> 2);

            // Left column: filtered
            for (int y = 1; y < nTbS; y++)
                WriteSample(buffer, planeOffset, stride, xP, yP + y, bytesPerSample,
                    (refLeft[y + 1] + 3 * dcVal + 2) >> 2);
        }
    }

    /// <summary>
    /// Angular intra prediction (modes 2-34).
    /// </summary>
    private static void PredictAngular(Span<byte> buffer, int planeOffset, int stride,
        int xP, int yP, int nTbS, int bitDepth, int intraPredMode, int cIdx, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        int maxVal = (1 << bitDepth) - 1;
        int angle = IntraAngleTable[intraPredMode - 2];

        // Use offset-based arrays to support negative indexing for negative angle modes.
        // FFmpeg uses pointer arithmetic (ref + size) to access ref[-1..-nTbS].
        // We achieve the same with an offset: refMain[offset + idx] where idx can be negative.
        int offset = nTbS; // offset so index -nTbS maps to array position 0
        Span<int> refMain = stackalloc int[3 * nTbS + 1]; // indices [-nTbS .. 2*nTbS]
        Span<int> refSide = stackalloc int[2 * nTbS + 1]; // indices [0 .. 2*nTbS]

        bool isVertical = intraPredMode >= 18; // modes 18-34 are primarily vertical

        if (isVertical)
        {
            // Main reference = above, side reference = left
            for (int i = 0; i <= 2 * nTbS; i++)
                refMain[offset + i] = refAbove[i];
            for (int i = 0; i <= 2 * nTbS; i++)
                refSide[i] = refLeft[i];
        }
        else
        {
            // Main reference = left, side reference = above
            for (int i = 0; i <= 2 * nTbS; i++)
                refMain[offset + i] = refLeft[i];
            for (int i = 0; i <= 2 * nTbS; i++)
                refSide[i] = refAbove[i];
        }

        // Extend main reference with projected side reference for negative angles
        // Matches FFmpeg: ref_main[x] = ref_side[((x+1)*inv_angle + 128) >> 8]
        if (angle < 0)
        {
            int invAngle = InverseAngleTable[intraPredMode - 2];
            int invAngleSum = 128;
            for (int i = -1; i >= -nTbS; i--)
            {
                invAngleSum += invAngle;
                int refIdx = invAngleSum >> 8;
                if (refIdx >= 0 && refIdx <= 2 * nTbS)
                    refMain[offset + i] = refSide[refIdx];
            }
        }

        // Generate prediction samples
        for (int y = 0; y < nTbS; y++)
        {
            for (int x = 0; x < nTbS; x++)
            {
                int deltaPos;
                if (isVertical)
                    deltaPos = (y + 1) * angle;
                else
                    deltaPos = (x + 1) * angle;

                int iDeltaInt = deltaPos >> 5;
                int iDeltaFrac = deltaPos & 31;

                int refIdx;
                if (isVertical)
                    refIdx = x + 1 + iDeltaInt;
                else
                    refIdx = y + 1 + iDeltaInt;

                int val;
                if (iDeltaFrac != 0)
                {
                    // Fractional position: interpolate
                    int s0 = refMain[offset + refIdx];
                    int s1 = refMain[offset + refIdx + 1];
                    val = ((32 - iDeltaFrac) * s0 + iDeltaFrac * s1 + 16) >> 5;
                }
                else
                {
                    val = refMain[offset + refIdx];
                }

                val = Math.Clamp(val, 0, maxVal);

                WriteSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample, val);
            }
        }

        // Edge filtering for pure vertical (mode 26) and horizontal (mode 10)
        // HEVC spec 8.4.4.2.7: luma only, block size < 32
        // Matches ffmpeg pred_template.c angular edge filter
        if (cIdx == 0 && nTbS < 32)
        {
            if (intraPredMode == 26) // Pure vertical — filter column 0
            {
                for (int y = 0; y < nTbS; y++)
                {
                    int val = refAbove[1] + ((refLeft[y + 1] - refLeft[0]) >> 1);
                    WriteSample(buffer, planeOffset, stride, xP, yP + y, bytesPerSample,
                        Math.Clamp(val, 0, maxVal));
                }
            }
            else if (intraPredMode == 10) // Pure horizontal — filter row 0
            {
                for (int x = 0; x < nTbS; x++)
                {
                    int val = refLeft[1] + ((refAbove[x + 1] - refLeft[0]) >> 1);
                    WriteSample(buffer, planeOffset, stride, xP + x, yP, bytesPerSample,
                        Math.Clamp(val, 0, maxVal));
                }
            }
        }
    }

    // HEVC Table 8-6: QpC from qPi for chroma QP derivation
    // HEVC Table 8-6: QPC from qPi for chroma_format_idc == 1 (4:2:0)
    // Indices 0-51 from spec, indices 52-57 extend the paired pattern for 10/12-bit QP range
    private static ReadOnlySpan<byte> ChromaQpTable => [
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
        31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 39, 40, 41,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51
    ];

    /// <summary>
    /// Applies inverse transform to residual coefficients and adds to prediction.
    /// </summary>
    private void ApplyInverseTransformAndReconstruct(HevcSequenceParameterSet sps,
        int x0, int y0, int log2TrafoSize, int cIdx, int intraPredMode, bool transformSkip,
        bool explicitRdpcmFlag, int explicitRdpcmDirFlag,
        Span<short> residual, bool saveLumaResidual = false, int ccpResScaleVal = 0)
    {
        if (currentFrameBuffer == null) return;

        int trafoSize = 1 << log2TrafoSize;
        int bitDepth = (cIdx == 0) ? (sps.BitDepthLumaMinus8 + 8) : (sps.BitDepthChromaMinus8 + 8);
        int maxVal = (1 << bitDepth) - 1;

        // Derive QP for dequantization
        int qp;
        if (cIdx == 0)
        {
            qp = qpY;
        }
        else
        {
            // HEVC spec 8.6.1: chroma QP derivation (FFmpeg cabac.c:1052-1075)
            // Per-CU chroma QP offset from RExt cu_chroma_qp_offset (FFmpeg cabac.c:1057,1060)
            int qpOffset = cIdx == 1
                ? (currentPps?.PpsCbQpOffset ?? 0) + (currentSliceHeader?.SliceCbQpOffset ?? 0) + cuQpOffsetCb
                : (currentPps?.PpsCrQpOffset ?? 0) + (currentSliceHeader?.SliceCrQpOffset ?? 0) + cuQpOffsetCr;
            int qpBdOffsetC = 6 * sps.BitDepthChromaMinus8;
            int qPi = Math.Clamp(qpY + qpOffset, -qpBdOffsetC, 57);

            if (sps.ChromaFormatIdc == HevcChromaFormat.Chroma420)
            {
                // Table 8-6 mapping for 420 only
                qp = qPi < 0 ? qPi : (qPi < ChromaQpTable.Length ? ChromaQpTable[qPi] : ChromaQpTable[^1]);
            }
            else
            {
                // 422/444: QpC = min(qPi, 51) — no table mapping
                qp = Math.Min(qPi, 51);
            }
        }

        // DIAG: Trace raw coefficients BEFORE dequant for first error TU
        // When cu_transquant_bypass is active, raw coefficient levels ARE the residual.
        // Skip dequantization and inverse transform entirely (FFmpeg cabac.c lines 1050, 1439).
        if (!currentCuTransquantBypass)
        {
            DequantizeCoefficients(residual, trafoSize, qp, bitDepth, log2TrafoSize, cIdx, transformSkip);
        }

        // Apply inverse transform (or transform skip) — skip for transquant bypass
        Span<short> transformed = stackalloc short[trafoSize * trafoSize];
        if (currentCuTransquantBypass)
        {
            // Bypass: coefficients are already the residual, copy directly
            residual.Slice(0, trafoSize * trafoSize).CopyTo(transformed);

            // RExt RDPCM for transquant bypass mode (FFmpeg cabac.c:1439-1445)
            if (explicitRdpcmFlag ||
                (sps.ImplicitRdpcmEnabled && (intraPredMode == 10 || intraPredMode == 26)))
            {
                int mode = sps.ImplicitRdpcmEnabled ? (intraPredMode == 26 ? 1 : 0) : explicitRdpcmDirFlag;
                TransformRdpcm(transformed, log2TrafoSize, mode);
            }
        }
        else if (transformSkip)
        {
            // RExt: transform_skip_rotation for 4x4 intra (FFmpeg cabac.c:1447-1454)
            if (sps.TransformSkipRotationEnabled && log2TrafoSize == 2 &&
                currentPredMode == HevcPredictionMode.Intra)
            {
                var coeffs = residual.Slice(0, 16);
                for (int ri = 0; ri < 8; ri++)
                    (coeffs[ri], coeffs[15 - ri]) = (coeffs[15 - ri], coeffs[ri]);
            }

            HevcTransform.TransformSkip(residual.Slice(0, trafoSize * trafoSize), transformed,
                log2TrafoSize, bitDepth);

            // RExt RDPCM for transform_skip mode (FFmpeg cabac.c:1458-1464)
            if (explicitRdpcmFlag ||
                (sps.ImplicitRdpcmEnabled && currentPredMode == HevcPredictionMode.Intra &&
                 (intraPredMode == 10 || intraPredMode == 26)))
            {
                int mode = explicitRdpcmFlag ? explicitRdpcmDirFlag : (intraPredMode == 26 ? 1 : 0);
                TransformRdpcm(transformed, log2TrafoSize, mode);
            }
        }
        else
        {
            bool isIntra = currentPredMode == HevcPredictionMode.Intra;
            // DST-VII is only for 4x4 intra LUMA (cIdx == 0), not chroma (HEVC spec 8.6.4.2)
            bool useDst = isIntra && log2TrafoSize == 2 && cIdx == 0;
            HevcTransform.InverseTransform(residual.Slice(0, trafoSize * trafoSize), transformed,
                trafoSize, useDst, bitDepth);
        }

        // Cross-component prediction: save luma residual for later use by chroma
        if (saveLumaResidual)
        {
            int count = trafoSize * trafoSize;
            transformed.Slice(0, count).CopyTo(ccpLumaResidual.AsSpan());
        }

        // Cross-component prediction: add scaled luma residual to chroma residual
        // FFmpeg cabac.c:1483-1488: coeffs[i] += (res_scale_val * coeffs_y[i]) >> 3
        if (ccpResScaleVal != 0)
        {
            int count = trafoSize * trafoSize;
            for (int i = 0; i < count; i++)
                transformed[i] = (short)(transformed[i] + ((ccpResScaleVal * ccpLumaResidual[i]) >> 3));
        }

        // Add residual to prediction and clip
        GetPlaneParams(sps, cIdx, out int planeOffset, out int stride, out int planeWidth, out int planeHeight);
        int bytesPerSample = bitDepth > 8 ? 2 : 1;

        // Adjust coordinates for chroma subsampling
        int xP = (cIdx > 0) ? x0 >> sps.HShiftChroma : x0;
        int yP = (cIdx > 0) ? y0 >> sps.VShiftChroma : y0;

        var buffer = currentFrameBuffer.AsSpan();

        for (int y = 0; y < trafoSize; y++)
        {
            for (int x = 0; x < trafoSize; x++)
            {
                if (xP + x >= planeWidth || yP + y >= planeHeight) continue;

                int pred = ReadSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample);
                int res = transformed[y * trafoSize + x];
                int recon = Math.Clamp(pred + res, 0, maxVal);

                WriteSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample, recon);
            }
        }
    }

    /// <summary>
    /// Apply cross-component prediction when chroma has no coded residual (cbf=false).
    /// Generates chroma residual from scaled luma residual and adds to chroma prediction.
    /// Matches FFmpeg hevcdec.c:1410-1424, 1440-1454.
    /// </summary>
    private void ApplyCrossComponentPredOnly(HevcSequenceParameterSet sps,
        int x0, int y0, int log2TrafoSizeC, int cIdx, int resScaleVal)
    {
        if (currentFrameBuffer == null || resScaleVal == 0) return;

        int trafoSize = 1 << log2TrafoSizeC;
        int bitDepth = sps.BitDepthChromaMinus8 + 8;
        int maxVal = (1 << bitDepth) - 1;

        GetPlaneParams(sps, cIdx, out int planeOffset, out int stride, out int planeWidth, out int planeHeight);
        int bytesPerSample = bitDepth > 8 ? 2 : 1;

        int xP = x0 >> sps.HShiftChroma;
        int yP = y0 >> sps.VShiftChroma;

        var buffer = currentFrameBuffer.AsSpan();

        for (int y = 0; y < trafoSize; y++)
        {
            for (int x = 0; x < trafoSize; x++)
            {
                if (xP + x >= planeWidth || yP + y >= planeHeight) continue;

                int ccpRes = (resScaleVal * ccpLumaResidual[y * trafoSize + x]) >> 3;
                int pred = ReadSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample);
                int recon = Math.Clamp(pred + ccpRes, 0, maxVal);
                WriteSample(buffer, planeOffset, stride, xP + x, yP + y, bytesPerSample, recon);
            }
        }
    }

    /// <summary>
    /// Dequantizes residual coefficients in-place.
    /// Matches FFmpeg's dequantization in ff_hevc_hls_residual_coding (cabac.c lines 1050-1425).
    /// Uses scaling list matrices when available.
    /// </summary>
    private void DequantizeCoefficients(Span<short> coeffs, int trafoSize, int qp, int bitDepth,
        int log2TrafoSize, int cIdx, bool transformSkip)
    {
        // FFmpeg adds qp_bd_offset to convert QpY → Qp'Y before dequant (cabac.c line 1051/1077)
        int qpPrime = qp + 6 * (bitDepth - 8);
        
        int shift = bitDepth + log2TrafoSize - 5;
        int add = shift > 0 ? (1 << (shift - 1)) : 0;

        ReadOnlySpan<int> levelScale = [40, 45, 51, 57, 64, 72];
        int qpDiv6 = qpPrime / 6;
        int qpMod6 = qpPrime % 6;
        int scale = levelScale[qpMod6] << qpDiv6;

        // Resolve scaling matrix for this TU
        byte[]? scaleMatrix = null;
        int dcScale = 16;

        // FFmpeg cabac.c:1086: scaling list disabled when transform_skip && log2TrafoSize > 2
        if (activeScalingList != null && !(transformSkip && log2TrafoSize > 2))
        {
            int matrixId = 3 * (currentPredMode != HevcPredictionMode.Intra ? 1 : 0) + cIdx;
            scaleMatrix = activeScalingList.Sl[log2TrafoSize - 2][matrixId];
            if (log2TrafoSize >= 4)
                dcScale = activeScalingList.SlDc[log2TrafoSize - 4][matrixId];
        }

        int count = trafoSize * trafoSize;
        for (int i = 0; i < count; i++)
        {
            if (coeffs[i] == 0) continue;

            int scaleM;
            if (scaleMatrix != null)
            {
                int xC = i % trafoSize;
                int yC = i / trafoSize;
                
                // DC coefficient uses separate dc_scale for 16x16+ transforms
                if (xC == 0 && yC == 0 && log2TrafoSize >= 4)
                {
                    scaleM = dcScale;
                }
                else
                {
                    int pos = log2TrafoSize switch
                    {
                        3 => (yC << 3) + xC,
                        4 => ((yC >> 1) << 3) + (xC >> 1),
                        5 => ((yC >> 2) << 3) + (xC >> 2),
                        _ => (yC << 2) + xC, // 4x4
                    };
                    scaleM = scaleMatrix[pos];
                }
            }
            else
            {
                scaleM = 16; // flat matrix when scaling lists disabled
            }

            long val = (long)coeffs[i] * scale * scaleM;
            val = (val + add) >> shift;
            coeffs[i] = (short)Math.Clamp(val, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Decodes PCM (pulse code modulation) samples from the bitstream and writes them
    /// directly to the frame buffer. Bypasses all transform and prediction.
    /// Matches FFmpeg's hls_pcm_sample() in hevcdec.c.
    /// </summary>
    private void DecodePcmSamples(ref HevcCabacDecoder cabac, HevcSequenceParameterSet sps,
        int x0, int y0, int log2CbSize)
    {
        if (currentFrameBuffer == null) return;

        int cbSize = 1 << log2CbSize;
        int lumaBitDepth = sps.BitDepthLumaMinus8 + 8;
        int chromaBitDepth = sps.BitDepthChromaMinus8 + 8;
        int pcmBitDepthLuma = sps.PcmSampleBitDepthLumaMinus1 + 1;
        int pcmBitDepthChroma = sps.PcmSampleBitDepthChromaMinus1 + 1;

        // Calculate total PCM data length in bits
        int lumaLength = cbSize * cbSize * pcmBitDepthLuma;
        int chromaLength = 0;
        if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome)
        {
            int hShift = sps.HShiftChroma;
            int vShift = sps.VShiftChroma;
            int chromaW = cbSize >> hShift;
            int chromaH = cbSize >> vShift;
            chromaLength = (chromaW * chromaH + chromaW * chromaH) * pcmBitDepthChroma;
        }
        int totalBits = lumaLength + chromaLength;
        int totalBytes = (totalBits + 7) >> 3;

        // Extract raw PCM bytes from bitstream (skip_bytes reinitializes CABAC after)
        ReadOnlySpan<byte> pcmData = cabac.SkipBytes(totalBytes);

        // Read PCM samples using a simple bit reader
        int bitPos = 0;

        // Write luma samples
        GetPlaneParams(sps, 0, out int lumaPlaneOffset, out int lumaStride,
            out int lumaPlaneWidth, out int lumaPlaneHeight);
        int lumaBytesPerSample = lumaBitDepth > 8 ? 2 : 1;
        var buffer = currentFrameBuffer.AsSpan();
        int lumaShift = lumaBitDepth - pcmBitDepthLuma;

        for (int y = 0; y < cbSize; y++)
        {
            for (int x = 0; x < cbSize; x++)
            {
                int sample = ReadBits(pcmData, ref bitPos, pcmBitDepthLuma) << lumaShift;
                if (x0 + x < lumaPlaneWidth && y0 + y < lumaPlaneHeight)
                    WriteSample(buffer, lumaPlaneOffset, lumaStride, x0 + x, y0 + y, lumaBytesPerSample, sample);
            }
        }

        // Write chroma samples (Cb then Cr)
        if (sps.ChromaFormatIdc != HevcChromaFormat.Monochrome)
        {
            int hShift = sps.HShiftChroma;
            int vShift = sps.VShiftChroma;
            int chromaW = cbSize >> hShift;
            int chromaH = cbSize >> vShift;
            int chromaBytesPerSample = chromaBitDepth > 8 ? 2 : 1;
            int chromaShift = chromaBitDepth - pcmBitDepthChroma;

            for (int cIdx = 1; cIdx <= 2; cIdx++)
            {
                GetPlaneParams(sps, cIdx, out int chromaPlaneOffset, out int chromaStride,
                    out int chromaPlaneWidth, out int chromaPlaneHeight);
                int xC = x0 >> hShift;
                int yC = y0 >> vShift;

                for (int y = 0; y < chromaH; y++)
                {
                    for (int x = 0; x < chromaW; x++)
                    {
                        int sample = ReadBits(pcmData, ref bitPos, pcmBitDepthChroma) << chromaShift;
                        if (xC + x < chromaPlaneWidth && yC + y < chromaPlaneHeight)
                            WriteSample(buffer, chromaPlaneOffset, chromaStride, xC + x, yC + y, chromaBytesPerSample, sample);
                    }
                }
            }
        }

        // Mark deblocking boundary strengths for PCM block
        FillDeblockingInfo(x0, y0, cbSize, true);
        FillDeblockingInfoTU(x0, y0, log2CbSize, true, false);
    }

    /// <summary>
    /// Reads N bits from a byte span at the specified bit position.
    /// Used for PCM sample extraction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitPos, int numBits)
    {
        int value = 0;
        for (int i = 0; i < numBits; i++)
        {
            int byteIdx = bitPos >> 3;
            int bitIdx = 7 - (bitPos & 7);
            if (byteIdx < data.Length)
                value = (value << 1) | ((data[byteIdx] >> bitIdx) & 1);
            else
                value <<= 1;
            bitPos++;
        }
        return value;
    }

    /// <summary>
    /// Residual DPCM: cumulative sum along rows (mode=0) or columns (mode=1).
    /// Matches FFmpeg's transform_rdpcm (dsp_template.c:87-107).
    /// </summary>
    private static void TransformRdpcm(Span<short> coeffs, int log2Size, int mode)
    {
        int size = 1 << log2Size;
        if (mode != 0)
        {
            // Vertical: cumulative sum down each column
            for (int y = 1; y < size; y++)
            {
                int row = y * size;
                int prevRow = (y - 1) * size;
                for (int x = 0; x < size; x++)
                    coeffs[row + x] += coeffs[prevRow + x];
            }
        }
        else
        {
            // Horizontal: cumulative sum across each row
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 1; x < size; x++)
                    coeffs[row + x] += coeffs[row + x - 1];
            }
        }
    }
}
