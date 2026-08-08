// HEVC Residual Coding — CABAC-driven coefficient decoding
// Reference: ITU-T H.265 Section 7.3.8.11, FFmpeg hevc/cabac.c ff_hevc_hls_residual_coding

using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc;

internal sealed partial class HevcDecoder
{
    private const int ScanDiag = 0;
    private const int ScanHoriz = 1;
    private const int ScanVert = 2;

    // 4x4 diagonal scan forward (scan index → x,y)
    private static readonly byte[] DiagScan4x4X =
        { 0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 1, 2, 3, 2, 3, 3 };
    private static readonly byte[] DiagScan4x4Y =
        { 0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 3, 2, 1, 3, 2, 3 };

    // 4x4 diagonal scan inverse [y,x] → scan index (matches FFmpeg diag_scan4x4_inv)
    private static readonly byte[,] DiagScan4x4Inv = new byte[4, 4]
    {
        { 0,  2,  5, 9 },
        { 1,  4,  8, 12 },
        { 3,  7, 11, 14 },
        { 6, 10, 13, 15 },
    };

    // 2x2 diagonal scan (for sub-block grids of 8x8 TU)
    private static readonly byte[] DiagScan2x2X = { 0, 0, 1, 1 };
    private static readonly byte[] DiagScan2x2Y = { 0, 1, 0, 1 };
    private static readonly byte[,] DiagScan2x2Inv = new byte[2, 2]
    {
        { 0, 2 },
        { 1, 3 },
    };

    // 8x8 diagonal scan forward (scan index → x,y)
    private static readonly byte[] DiagScan8x8X =
    {
        0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 0, 1, 2, 3, 4, 0,
        1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3,
        4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 2, 3, 4, 5, 6,
        7, 3, 4, 5, 6, 7, 4, 5, 6, 7, 5, 6, 7, 6, 7, 7,
    };
    private static readonly byte[] DiagScan8x8Y =
    {
        0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 4, 3, 2, 1, 0, 5,
        4, 3, 2, 1, 0, 6, 5, 4, 3, 2, 1, 0, 7, 6, 5, 4,
        3, 2, 1, 0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3,
        2, 7, 6, 5, 4, 3, 7, 6, 5, 4, 7, 6, 5, 7, 6, 7,
    };

    // 8x8 diagonal scan inverse [y,x] → scan index (matches FFmpeg diag_scan8x8_inv)
    private static readonly byte[,] DiagScan8x8Inv = new byte[8, 8]
    {
        {  0,  2,  5,  9, 14, 20, 27, 35 },
        {  1,  4,  8, 13, 19, 26, 34, 42 },
        {  3,  7, 12, 18, 25, 33, 41, 48 },
        {  6, 11, 17, 24, 32, 40, 47, 53 },
        { 10, 16, 23, 31, 39, 46, 52, 57 },
        { 15, 22, 30, 38, 45, 51, 56, 60 },
        { 21, 29, 37, 44, 50, 55, 59, 62 },
        { 28, 36, 43, 49, 54, 58, 61, 63 },
    };

    // Horizontal scan tables (4x4, 8x8 inverse)
    private static readonly byte[] HorizScan4x4X =
        { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 };
    private static readonly byte[] HorizScan4x4Y =
        { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3 };
    private static readonly byte[] HorizScan2x2X = { 0, 1, 0, 1 };
    private static readonly byte[] HorizScan2x2Y = { 0, 0, 1, 1 };

    // sig_coeff_flag context map from FFmpeg — indexed by (y_c << 2) + x_c within sub-block
    private static readonly byte[] SigCoeffCtxIdxMap =
    {
        0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8, // log2TrafoSize == 2
        1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, // prev_sig == 0
        2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, // prev_sig == 1
        2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0, // prev_sig == 2
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // default (prev_sig == 3)
    };

    /// <summary>
    /// Decodes residual coefficients for a transform block.
    /// Closely matches FFmpeg's ff_hevc_hls_residual_coding.
    /// </summary>
    private bool DecodeResidualCoding(ref HevcCabacDecoder cabac, HevcSequenceParameterSet sps,
        HevcPictureParameterSet pps, int x0, int y0, int log2TrafoSize, int scanIdx,
        int cIdx, Span<short> residual,
        out bool explicitRdpcmFlag, out int explicitRdpcmDirFlag)
    {
        explicitRdpcmFlag = false;
        explicitRdpcmDirFlag = 0;

        int trafoSize = 1 << log2TrafoSize;
        int numCoeff = trafoSize * trafoSize;
        residual.Slice(0, numCoeff).Clear();

        // Transform skip flag — only coded when NOT cu_transquant_bypass (HEVC spec 7.3.8.11)
        // FFmpeg: inside if (!lc->cu.cu_transquant_bypass_flag) block (cabac.c:1045-1048)
        bool transformSkip = false;
        if (!currentCuTransquantBypass && pps.TransformSkipEnabled &&
            log2TrafoSize <= pps.Log2MaxTransformSkipBlockSize)
        {
            transformSkip = cabac.DecodeBin(HevcCabacContextIndex.TransformSkipFlag +
                (cIdx > 0 ? 1 : 0)) != 0;
        }

        // RExt: explicit_rdpcm_flag/dir_flag for INTER + (transquant_bypass || transform_skip)
        // FFmpeg cabac.c:1104-1110
        if (currentPredMode == HevcPredictionMode.Inter && sps.ExplicitRdpcmEnabled &&
            (transformSkip || currentCuTransquantBypass))
        {
            explicitRdpcmFlag = cabac.DecodeBin(
                HevcCabacContextIndex.ExplicitRdpcmFlag + (cIdx > 0 ? 1 : 0)) != 0;
            if (explicitRdpcmFlag)
            {
                explicitRdpcmDirFlag = cabac.DecodeBin(
                    HevcCabacContextIndex.ExplicitRdpcmDirFlag + (cIdx > 0 ? 1 : 0));
            }
        }

        // Diagnostic: log transform_skip and transquant_bypass per TU
        DiagResidualLog?.Add((0, x0, y0, log2TrafoSize, cIdx, transformSkip, currentCuTransquantBypass));

        // Decode last significant coefficient position
        // CRITICAL: FFmpeg decodes BOTH prefixes (context bins) first, then BOTH suffixes (bypass bins).
        // Mixing context/bypass order causes CABAC state divergence.
        int lastSigCoeffXPrefix = DecodeLastSignificantCoeffPrefix(ref cabac, log2TrafoSize, cIdx, true);
        int lastSigCoeffYPrefix = DecodeLastSignificantCoeffPrefix(ref cabac, log2TrafoSize, cIdx, false);
        int lastSignificantCoeffX = DecodeLastSignificantCoeffValue(ref cabac, lastSigCoeffXPrefix);
        int lastSignificantCoeffY = DecodeLastSignificantCoeffValue(ref cabac, lastSigCoeffYPrefix);

        if (scanIdx == ScanVert)
            (lastSignificantCoeffX, lastSignificantCoeffY) = (lastSignificantCoeffY, lastSignificantCoeffX);

        int xCgLastSig = lastSignificantCoeffX >> 2;
        int yCgLastSig = lastSignificantCoeffY >> 2;

        // Select scan tables based on scan mode
        byte[] scanXOff, scanYOff, scanXCg, scanYCg;
        int numCoeffInScanOrder;

        switch (scanIdx)
        {
            case ScanDiag:
            {
                int lastXLocal = lastSignificantCoeffX & 3;
                int lastYLocal = lastSignificantCoeffY & 3;
                scanXOff = DiagScan4x4X;
                scanYOff = DiagScan4x4Y;
                numCoeffInScanOrder = DiagScan4x4Inv[lastYLocal, lastXLocal];

                if (trafoSize == 4)
                {
                    scanXCg = new byte[] { 0 };
                    scanYCg = new byte[] { 0 };
                }
                else if (trafoSize == 8)
                {
                    numCoeffInScanOrder += DiagScan2x2Inv[yCgLastSig, xCgLastSig] << 4;
                    scanXCg = DiagScan2x2X;
                    scanYCg = DiagScan2x2Y;
                }
                else if (trafoSize == 16)
                {
                    numCoeffInScanOrder += DiagScan4x4Inv[yCgLastSig, xCgLastSig] << 4;
                    scanXCg = DiagScan4x4X;
                    scanYCg = DiagScan4x4Y;
                }
                else // trafoSize == 32
                {
                    numCoeffInScanOrder += DiagScan8x8Inv[yCgLastSig, xCgLastSig] << 4;
                    scanXCg = DiagScan8x8X;
                    scanYCg = DiagScan8x8Y;
                }
                break;
            }
            case ScanHoriz:
                scanXCg = HorizScan2x2X;
                scanYCg = HorizScan2x2Y;
                scanXOff = HorizScan4x4X;
                scanYOff = HorizScan4x4Y;
                if (trafoSize == 4)
                {
                    numCoeffInScanOrder = lastSignificantCoeffY * 4 + lastSignificantCoeffX;
                }
                else
                {
                    // 8x8: combine sub-block index with local index
                    int lastXLocal = lastSignificantCoeffX & 3;
                    int lastYLocal = lastSignificantCoeffY & 3;
                    numCoeffInScanOrder = (yCgLastSig * 2 + xCgLastSig) * 16 +
                        (lastYLocal * 4 + lastXLocal);
                }
                break;
            default: // ScanVert (x,y already swapped above)
                scanXCg = HorizScan2x2Y;
                scanYCg = HorizScan2x2X;
                scanXOff = HorizScan4x4Y;
                scanYOff = HorizScan4x4X;
                if (trafoSize == 4)
                {
                    numCoeffInScanOrder = lastSignificantCoeffX * 4 + lastSignificantCoeffY;
                }
                else
                {
                    // 8x8: column-major CG order and column-major local order
                    // FFmpeg uses horiz_scan8x8_inv[last_x][last_y] for VERT (swapped subscripts vs HORIZ)
                    int lastXLocal = lastSignificantCoeffX & 3;
                    int lastYLocal = lastSignificantCoeffY & 3;
                    numCoeffInScanOrder = (xCgLastSig * 2 + yCgLastSig) * 16 +
                        (lastXLocal * 4 + lastYLocal);
                }
                break;
        }
        numCoeffInScanOrder++;
        int numLastSubset = (numCoeffInScanOrder - 1) >> 4;

        // 2D significant coefficient group flag array (matches FFmpeg)
        int cgGridSize = Math.Max(1, trafoSize >> 2);
        Span<bool> sigCoeffGroupFlag = stackalloc bool[cgGridSize * cgGridSize];
        sigCoeffGroupFlag.Clear();

        int greater1Ctx = 1;

        bool persistentRice = sps.PersistentRiceAdaptationEnabled;

        for (int i = numLastSubset; i >= 0; i--)
        {
            int implicitNonZeroCoeff = 0;
            int cRiceParam = 0;
            bool riceInit = false;
            int sbType = 0;
            if (persistentRice)
            {
                sbType = 2 * (cIdx == 0 ? 1 : 0) +
                    (!transformSkip && !currentCuTransquantBypass ? 0 : 1);
                cRiceParam = statCoeff[sbType] / 4;
            }
            int offset = i << 4;
            int xCg = scanXCg[i];
            int yCg = scanYCg[i];

            // Decode coded_sub_block_flag
            if (i < numLastSubset && i > 0)
            {
                int ctxCg = 0;
                if (xCg < cgGridSize - 1)
                    ctxCg += sigCoeffGroupFlag[yCg * cgGridSize + xCg + 1] ? 1 : 0;
                if (yCg < cgGridSize - 1)
                    ctxCg += sigCoeffGroupFlag[(yCg + 1) * cgGridSize + xCg] ? 1 : 0;

                int csbfCtxIdx = HevcCabacContextIndex.SignificantCoeffGroupFlag +
                    (cIdx > 0 ? 2 : 0) + (ctxCg > 0 ? 1 : 0);
                sigCoeffGroupFlag[yCg * cgGridSize + xCg] =
                    cabac.DecodeBin(csbfCtxIdx) != 0;
                implicitNonZeroCoeff = 1;
            }
            else
            {
                sigCoeffGroupFlag[yCg * cgGridSize + xCg] =
                    (xCg == xCgLastSig && yCg == yCgLastSig) || (xCg == 0 && yCg == 0);
            }

            int lastScanPos = numCoeffInScanOrder - offset - 1;
            int nEnd;
            Span<byte> sigCoeffFlagIdx = stackalloc byte[16];
            int nbSigCoeff = 0;

            if (i == numLastSubset)
            {
                nEnd = lastScanPos - 1;
                sigCoeffFlagIdx[0] = (byte)lastScanPos;
                nbSigCoeff = 1;
            }
            else
            {
                nEnd = 15;
            }

            // Compute prev_sig from neighboring sub-block flags
            int prevSig = 0;
            if (xCg < cgGridSize - 1)
                prevSig = sigCoeffGroupFlag[yCg * cgGridSize + xCg + 1] ? 1 : 0;
            if (yCg < cgGridSize - 1)
                prevSig += (sigCoeffGroupFlag[(yCg + 1) * cgGridSize + xCg] ? 1 : 0) << 1;

            // Decode sig_coeff_flag for positions nEnd..1
            if (sigCoeffGroupFlag[yCg * cgGridSize + xCg] && nEnd >= 0)
            {
                int scfOffset = 0;
                int ctxMapOffset;

                // RExt: transform_skip_context_enabled changes sig_coeff context
                // for transform_skip or transquant_bypass TUs (FFmpeg cabac.c:1235-1242)
                if (sps.TransformSkipContextEnabled &&
                    (transformSkip || currentCuTransquantBypass))
                {
                    ctxMapOffset = 4 << 4; // ctx_idx_map[4*16] = all 2s (default row)
                    scfOffset = (cIdx == 0) ? 40 : (14 + 27);
                }
                else if (log2TrafoSize == 2)
                {
                    ctxMapOffset = 0; // first row of SigCoeffCtxIdxMap
                }
                else
                {
                    ctxMapOffset = (prevSig + 1) << 4;
                    if (cIdx == 0)
                    {
                        if (xCg > 0 || yCg > 0)
                            scfOffset += 3;
                        if (log2TrafoSize == 3)
                            scfOffset += (scanIdx == ScanDiag) ? 9 : 15;
                        else
                            scfOffset += 21;
                    }
                    else
                    {
                        if (log2TrafoSize == 3)
                            scfOffset += 9;
                        else
                            scfOffset += 12;
                    }
                }

                if (cIdx != 0 && !(sps.TransformSkipContextEnabled &&
                    (transformSkip || currentCuTransquantBypass)))
                    scfOffset += 27;

                for (int n = nEnd; n > 0; n--)
                {
                    int xC = scanXOff[n];
                    int yC = scanYOff[n];
                    int ctxInc = SigCoeffCtxIdxMap[ctxMapOffset + (yC << 2) + xC] + scfOffset;

                    if (cabac.DecodeBin(HevcCabacContextIndex.SignificantCoeffFlag + ctxInc) != 0)
                    {
                        sigCoeffFlagIdx[nbSigCoeff] = (byte)n;
                        nbSigCoeff++;
                        implicitNonZeroCoeff = 0;
                    }
                }

                // Position 0 handling
                if (implicitNonZeroCoeff == 0)
                {
                    // Decode sig_coeff_flag for position 0 with special context
                    // RExt: transform_skip_context_enabled uses offset 42 (luma) or 16+27 (chroma)
                    // FFmpeg cabac.c:1276-1282
                    int scfOffset0;
                    if (sps.TransformSkipContextEnabled &&
                        (transformSkip || currentCuTransquantBypass))
                    {
                        scfOffset0 = (cIdx == 0) ? 42 : (16 + 27);
                    }
                    else if (i == 0)
                    {
                        scfOffset0 = (cIdx == 0) ? 0 : 27;
                    }
                    else
                    {
                        scfOffset0 = 2 + scfOffset;
                    }

                    if (cabac.DecodeBin(HevcCabacContextIndex.SignificantCoeffFlag + scfOffset0) != 0)
                    {
                        sigCoeffFlagIdx[nbSigCoeff] = 0;
                        nbSigCoeff++;
                    }
                }
                else
                {
                    // Position 0 implicitly significant
                    sigCoeffFlagIdx[nbSigCoeff] = 0;
                    nbSigCoeff++;
                }
            }

            if (nbSigCoeff == 0) continue;

            // Greater1 flags
            int firstGreater1CoeffIdx = -1;
            Span<byte> coeffAbsLevelGreater1Flag = stackalloc byte[8];
            coeffAbsLevelGreater1Flag.Clear();

            int ctxSet = (i > 0 && cIdx == 0) ? 2 : 0;
            if (i != numLastSubset && greater1Ctx == 0)
                ctxSet++;
            greater1Ctx = 1;

            int lastNzPosInCg = sigCoeffFlagIdx[0];
            int firstNzPosInCg = sigCoeffFlagIdx[nbSigCoeff - 1];

            int numG1 = Math.Min(8, nbSigCoeff);
            for (int m = 0; m < numG1; m++)
            {
                int inc = (ctxSet << 2) + greater1Ctx;
                coeffAbsLevelGreater1Flag[m] = (byte)cabac.DecodeBin(
                    HevcCabacContextIndex.CoeffAbsLevelGreater1Flag + (cIdx > 0 ? 16 : 0) + inc);
                if (coeffAbsLevelGreater1Flag[m] != 0)
                {
                    greater1Ctx = 0;
                    if (firstGreater1CoeffIdx == -1)
                        firstGreater1CoeffIdx = m;
                }
                else if (greater1Ctx > 0 && greater1Ctx < 3)
                {
                    greater1Ctx++;
                }
            }

            // Greater2 flag (only for first coeff that had greater1=1)
            if (firstGreater1CoeffIdx != -1)
            {
                coeffAbsLevelGreater1Flag[firstGreater1CoeffIdx] += (byte)cabac.DecodeBin(
                    HevcCabacContextIndex.CoeffAbsLevelGreater2Flag + (cIdx > 0 ? 4 : 0) + ctxSet);
            }

            // Sign bits
            // sign_hidden: forced to 0 for several RExt conditions (FFmpeg cabac.c:1348-1353)
            int predModeIntra = (cIdx == 0) ? currentTuIntraPredMode : currentTuIntraPredModeC;
            bool signHidden;
            if (currentCuTransquantBypass ||
                (currentPredMode == HevcPredictionMode.Intra && sps.ImplicitRdpcmEnabled &&
                 transformSkip && (predModeIntra == 10 || predModeIntra == 26)) ||
                explicitRdpcmFlag)
            {
                signHidden = false;
            }
            else
            {
                signHidden = lastNzPosInCg - firstNzPosInCg >= 4;
            }
            int numSignBits;
            if (!pps.SignDataHidingEnabled || !signHidden)
                numSignBits = nbSigCoeff;
            else
                numSignBits = nbSigCoeff - 1;

            uint coeffSignFlag = 0;
            for (int b = 0; b < numSignBits; b++)
                coeffSignFlag = (coeffSignFlag << 1) | (uint)cabac.DecodeBypass();
            coeffSignFlag <<= (16 - numSignBits);

            // Assemble coefficient levels with remaining
            int sumAbs = 0;
            for (int m = 0; m < nbSigCoeff; m++)
            {
                int n = sigCoeffFlagIdx[m];

                // Get position in the full transform block
                int xC = xCg * 4 + scanXOff[n];
                int yC = yCg * 4 + scanYOff[n];

                long transCoeffLevel;
                int lastRemaining = 0;
                if (m < 8)
                {
                    transCoeffLevel = 1 + coeffAbsLevelGreater1Flag[m];
                    int threshold = (m == firstGreater1CoeffIdx) ? 3 : 2;
                    if (transCoeffLevel == threshold)
                    {
                        lastRemaining = DecodeCoeffAbsLevelRemaining(ref cabac, cRiceParam);
                        transCoeffLevel += lastRemaining;
                        if (transCoeffLevel > (3 << cRiceParam))
                            cRiceParam = persistentRice ? cRiceParam + 1 : Math.Min(cRiceParam + 1, 4);
                        if (persistentRice && !riceInit)
                        {
                            int cRicePInit = statCoeff[sbType] / 4;
                            if (lastRemaining >= (3 << cRicePInit))
                                statCoeff[sbType]++;
                            else if (2 * lastRemaining < (1 << cRicePInit) && statCoeff[sbType] > 0)
                                statCoeff[sbType]--;
                            riceInit = true;
                        }
                    }
                }
                else
                {
                    lastRemaining = DecodeCoeffAbsLevelRemaining(ref cabac, cRiceParam);
                    transCoeffLevel = 1 + lastRemaining;
                    if (transCoeffLevel > (3 << cRiceParam))
                        cRiceParam = persistentRice ? cRiceParam + 1 : Math.Min(cRiceParam + 1, 4);
                    if (persistentRice && !riceInit)
                    {
                        int cRicePInit = statCoeff[sbType] / 4;
                        if (lastRemaining >= (3 << cRicePInit))
                            statCoeff[sbType]++;
                        else if (2 * lastRemaining < (1 << cRicePInit) && statCoeff[sbType] > 0)
                            statCoeff[sbType]--;
                        riceInit = true;
                    }
                }

                // Apply sign
                if (pps.SignDataHidingEnabled && signHidden)
                {
                    sumAbs += (int)transCoeffLevel;
                    if (n == firstNzPosInCg && (sumAbs & 1) != 0)
                        transCoeffLevel = -transCoeffLevel;
                }
                if ((coeffSignFlag >> 15) != 0)
                    transCoeffLevel = -transCoeffLevel;
                coeffSignFlag = (coeffSignFlag << 1) & 0xFFFF; // uint16_t wrapping (matches FFmpeg)

                if (xC < trafoSize && yC < trafoSize)
                    residual[yC * trafoSize + xC] = (short)transCoeffLevel;
            }
        }

        return transformSkip;
    }

    /// <summary>
    /// Decodes last_significant_coeff_x/y_prefix only (context bins).
    /// Matches FFmpeg's last_significant_coeff_xy_prefix_decode ordering.
    /// </summary>
    private int DecodeLastSignificantCoeffPrefix(ref HevcCabacDecoder cabac, int log2TrafoSize,
        int cIdx, bool isX)
    {
        int ctxOffset = isX
            ? HevcCabacContextIndex.LastSignificantCoeffXPrefix
            : HevcCabacContextIndex.LastSignificantCoeffYPrefix;

        int ctxShift;
        if (cIdx == 0)
        {
            ctxOffset += 3 * (log2TrafoSize - 2) + ((log2TrafoSize - 1) >> 2);
            ctxShift = (log2TrafoSize + 1) >> 2;
        }
        else
        {
            ctxOffset += 15;
            ctxShift = log2TrafoSize - 2;
        }

        int maxPrefix = (log2TrafoSize << 1) - 1;
        int prefix = 0;
        while (prefix < maxPrefix)
        {
            if (cabac.DecodeBin(ctxOffset + (prefix >> ctxShift)) == 0)
                break;
            prefix++;
        }

        return prefix;
    }

    /// <summary>
    /// Expands a last_significant_coeff prefix into the final value by decoding the suffix (bypass bins).
    /// </summary>
    private int DecodeLastSignificantCoeffValue(ref HevcCabacDecoder cabac, int prefix)
    {
        if (prefix >= 4)
        {
            int suffixBits = (prefix >> 1) - 1;
            int suffix = (int)cabac.DecodeBypassBits(suffixBits);
            return (2 + (prefix & 1)) << suffixBits | suffix;
        }

        return prefix;
    }

    /// <summary>
    /// Decodes coeff_abs_level_remaining using Golomb-Rice coding.
    /// Matches FFmpeg's coeff_abs_level_remaining_decode exactly.
    /// </summary>
    private int DecodeCoeffAbsLevelRemaining(ref HevcCabacDecoder cabac, int riceParam)
    {
        const int cabacMaxBin = 31; // FFmpeg: CABAC_MAX_BIN
        int prefix = 0;
        while (prefix < cabacMaxBin && cabac.DecodeBypass() != 0)
            prefix++;

        if (prefix < 3)
        {
            int suffix = riceParam > 0 ? (int)cabac.DecodeBypassBits(riceParam) : 0;
            return (prefix << riceParam) + suffix;
        }
        else
        {
            int prefixMinus3 = prefix - 3;
            if (prefix == cabacMaxBin || prefixMinus3 + riceParam > 22)
                return 0; // Error case (matches FFmpeg)
            int suffixBits = prefixMinus3 + riceParam;
            int suffix = suffixBits > 0 ? (int)cabac.DecodeBypassBits(suffixBits) : 0;
            return (((1 << prefixMinus3) + 2) << riceParam) + suffix;
        }
    }
}
