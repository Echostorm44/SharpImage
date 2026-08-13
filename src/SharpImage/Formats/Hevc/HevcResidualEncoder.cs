// HEVC residual_coding encoder — the exact mirror of HevcDecoder.DecodeResidualCoding. Given a
// quantized transform block it emits the CABAC bins the decoder reads back: last significant
// position, coded_sub_block_flag, sig_coeff_flag, greater1/greater2, sign bits (with optional
// sign-data-hiding), and coeff_abs_level_remaining. RExt features (transform_skip, RDPCM,
// persistent-rice) are not emitted (the encoder disables them in the PPS/SPS).
using System;

namespace SharpImage.Formats.Hevc;

internal static class HevcResidualEncoder
{
    /// <summary>
    /// Encodes residual_coding for one transform block. <paramref name="coeff"/> is row-major
    /// size×size quantized levels. Returns false if the block is all-zero (caller must not call
    /// with an all-zero block: cbf implies at least one nonzero coefficient).
    /// </summary>
    public static void Encode(HevcCabacEncoder cabac, ReadOnlySpan<short> coeff, int log2TrafoSize,
        int scanIdx, int cIdx, bool signDataHiding)
    {
        int trafoSize = 1 << log2TrafoSize;

        // Last significant coefficient position (in x/y coordinates).
        FindLastSignificant(coeff, trafoSize, scanIdx, out int lastX, out int lastY);

        // The decoder swaps x<->y for vertical scan after reading; write the swapped values.
        int codeLastX = lastX, codeLastY = lastY;
        if (scanIdx == HevcScanTables.ScanVert)
        {
            (codeLastX, codeLastY) = (codeLastY, codeLastX);
        }

        EncodeLastSigPrefixValue(cabac, log2TrafoSize, cIdx, codeLastX, true, out int lastXPrefix);
        EncodeLastSigPrefixValue(cabac, log2TrafoSize, cIdx, codeLastY, false, out int lastYPrefix);
        EncodeLastSigSuffix(cabac, lastXPrefix, codeLastX);
        EncodeLastSigSuffix(cabac, lastYPrefix, codeLastY);

        // Scan-order bookkeeping (mirrors the decoder).
        int xCgLastSig = lastX >> 2;
        int yCgLastSig = lastY >> 2;
        byte[] scanXOff, scanYOff, scanXCg, scanYCg;
        int numCoeffInScanOrder;
        ComputeScanOrder(scanIdx, trafoSize, lastX, lastY, xCgLastSig, yCgLastSig,
            out scanXOff, out scanYOff, out scanXCg, out scanYCg, out numCoeffInScanOrder);
        numCoeffInScanOrder++;
        int numLastSubset = (numCoeffInScanOrder - 1) >> 4;

        int cgGridSize = Math.Max(1, trafoSize >> 2);
        Span<bool> sigCoeffGroupFlag = stackalloc bool[cgGridSize * cgGridSize];
        sigCoeffGroupFlag.Clear();

        int greater1Ctx = 1;

        for (int i = numLastSubset; i >= 0; i--)
        {
            int implicitNonZeroCoeff = 0;
            int cRiceParam = 0;
            int offset = i << 4;
            int xCg = scanXCg[i];
            int yCg = scanYCg[i];

            bool cgSig = SubBlockHasCoeff(coeff, trafoSize, xCg, yCg, scanXOff, scanYOff);

            if (i < numLastSubset && i > 0)
            {
                int ctxCg = 0;
                if (xCg < cgGridSize - 1)
                {
                    ctxCg += sigCoeffGroupFlag[(yCg * cgGridSize) + xCg + 1] ? 1 : 0;
                }

                if (yCg < cgGridSize - 1)
                {
                    ctxCg += sigCoeffGroupFlag[((yCg + 1) * cgGridSize) + xCg] ? 1 : 0;
                }

                int csbfCtxIdx = HevcCabacContextIndex.SignificantCoeffGroupFlag +
                    (cIdx > 0 ? 2 : 0) + (ctxCg > 0 ? 1 : 0);
                cabac.EncodeBin(csbfCtxIdx, cgSig ? 1 : 0);
                sigCoeffGroupFlag[(yCg * cgGridSize) + xCg] = cgSig;
                implicitNonZeroCoeff = 1;
            }
            else
            {
                sigCoeffGroupFlag[(yCg * cgGridSize) + xCg] =
                    (xCg == xCgLastSig && yCg == yCgLastSig) || (xCg == 0 && yCg == 0);
            }

            if (!sigCoeffGroupFlag[(yCg * cgGridSize) + xCg])
            {
                continue;
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

            int prevSig = 0;
            if (xCg < cgGridSize - 1)
            {
                prevSig = sigCoeffGroupFlag[(yCg * cgGridSize) + xCg + 1] ? 1 : 0;
            }

            if (yCg < cgGridSize - 1)
            {
                prevSig += (sigCoeffGroupFlag[((yCg + 1) * cgGridSize) + xCg] ? 1 : 0) << 1;
            }

            if (nEnd >= 0)
            {
                int scfOffset = 0;
                int ctxMapOffset;
                if (log2TrafoSize == 2)
                {
                    ctxMapOffset = 0;
                }
                else
                {
                    ctxMapOffset = (prevSig + 1) << 4;
                    if (cIdx == 0)
                    {
                        if (xCg > 0 || yCg > 0)
                        {
                            scfOffset += 3;
                        }

                        scfOffset += log2TrafoSize == 3 ? (scanIdx == HevcScanTables.ScanDiag ? 9 : 15) : 21;
                    }
                    else
                    {
                        scfOffset += log2TrafoSize == 3 ? 9 : 12;
                    }
                }

                if (cIdx != 0)
                {
                    scfOffset += 27;
                }

                for (int n = nEnd; n > 0; n--)
                {
                    int xC = (xCg * 4) + scanXOff[n];
                    int yC = (yCg * 4) + scanYOff[n];
                    bool sig = coeff[(yC * trafoSize) + xC] != 0;
                    int ctxInc = HevcScanTables.SigCoeffCtxIdxMap[ctxMapOffset + (scanYOff[n] << 2) + scanXOff[n]] + scfOffset;
                    cabac.EncodeBin(HevcCabacContextIndex.SignificantCoeffFlag + ctxInc, sig ? 1 : 0);
                    if (sig)
                    {
                        sigCoeffFlagIdx[nbSigCoeff++] = (byte)n;
                        implicitNonZeroCoeff = 0;
                    }
                }

                if (implicitNonZeroCoeff == 0)
                {
                    int xC0 = xCg * 4;
                    int yC0 = yCg * 4;
                    bool sig0 = coeff[(yC0 * trafoSize) + xC0] != 0;
                    int scfOffset0 = i == 0 ? (cIdx == 0 ? 0 : 27) : (2 + scfOffset);
                    cabac.EncodeBin(HevcCabacContextIndex.SignificantCoeffFlag + scfOffset0, sig0 ? 1 : 0);
                    if (sig0)
                    {
                        sigCoeffFlagIdx[nbSigCoeff++] = 0;
                    }
                }
                else
                {
                    sigCoeffFlagIdx[nbSigCoeff++] = 0;
                }
            }

            if (nbSigCoeff == 0)
            {
                continue;
            }

            // Collect absolute levels + signs in scan order (high->low), as the decoder assembles them.
            Span<int> absLevels = stackalloc int[16];
            Span<int> signs = stackalloc int[16];
            for (int m = 0; m < nbSigCoeff; m++)
            {
                int n = sigCoeffFlagIdx[m];
                int xC = (xCg * 4) + scanXOff[n];
                int yC = (yCg * 4) + scanYOff[n];
                int v = coeff[(yC * trafoSize) + xC];
                absLevels[m] = Math.Abs(v);
                signs[m] = v < 0 ? 1 : 0;
            }

            int firstGreater1CoeffIdx = -1;
            Span<int> greater1 = stackalloc int[8];

            int ctxSet = (i > 0 && cIdx == 0) ? 2 : 0;
            if (i != numLastSubset && greater1Ctx == 0)
            {
                ctxSet++;
            }

            greater1Ctx = 1;
            int lastNzPosInCg = sigCoeffFlagIdx[0];
            int firstNzPosInCg = sigCoeffFlagIdx[nbSigCoeff - 1];

            int numG1 = Math.Min(8, nbSigCoeff);
            for (int m = 0; m < numG1; m++)
            {
                int g1 = absLevels[m] > 1 ? 1 : 0;
                int inc = (ctxSet << 2) + greater1Ctx;
                cabac.EncodeBin(HevcCabacContextIndex.CoeffAbsLevelGreater1Flag + (cIdx > 0 ? 16 : 0) + inc, g1);
                greater1[m] = g1;
                if (g1 != 0)
                {
                    greater1Ctx = 0;
                    if (firstGreater1CoeffIdx == -1)
                    {
                        firstGreater1CoeffIdx = m;
                    }
                }
                else if (greater1Ctx > 0 && greater1Ctx < 3)
                {
                    greater1Ctx++;
                }
            }

            if (firstGreater1CoeffIdx != -1)
            {
                int g2 = absLevels[firstGreater1CoeffIdx] > 2 ? 1 : 0;
                cabac.EncodeBin(HevcCabacContextIndex.CoeffAbsLevelGreater2Flag + (cIdx > 0 ? 4 : 0) + ctxSet, g2);
            }

            bool signHidden = signDataHiding && (lastNzPosInCg - firstNzPosInCg >= 4);
            int numSignBits = signHidden ? nbSigCoeff - 1 : nbSigCoeff;
            for (int b = 0; b < numSignBits; b++)
            {
                cabac.EncodeBypass(signs[b]);
            }

            // coeff_abs_level_remaining for each coefficient (matches decoder assembly order + rice update).
            for (int m = 0; m < nbSigCoeff; m++)
            {
                int levelSoFar;
                bool present;
                if (m < 8)
                {
                    int g2 = m == firstGreater1CoeffIdx ? (absLevels[m] > 2 ? 1 : 0) : 0;
                    levelSoFar = 1 + greater1[m] + g2;
                    int threshold = m == firstGreater1CoeffIdx ? 3 : 2;
                    present = levelSoFar == threshold;
                }
                else
                {
                    levelSoFar = 1;
                    present = true;
                }

                if (present)
                {
                    WriteCoeffAbsLevelRemaining(cabac, absLevels[m] - levelSoFar, cRiceParam);
                    if (absLevels[m] > (3 << cRiceParam))
                    {
                        cRiceParam = Math.Min(cRiceParam + 1, 4);
                    }
                }
            }
        }
    }

    private static void FindLastSignificant(ReadOnlySpan<short> coeff, int trafoSize, int scanIdx, out int lastX, out int lastY)
    {
        lastX = 0;
        lastY = 0;
        int bestScan = -1;
        for (int yC = 0; yC < trafoSize; yC++)
        {
            for (int xC = 0; xC < trafoSize; xC++)
            {
                if (coeff[(yC * trafoSize) + xC] == 0)
                {
                    continue;
                }

                int scan = FullScanIndex(scanIdx, trafoSize, xC, yC);
                if (scan > bestScan)
                {
                    bestScan = scan;
                    lastX = xC;
                    lastY = yC;
                }
            }
        }
    }

    private static int FullScanIndex(int scanIdx, int trafoSize, int xC, int yC)
    {
        int xCg = xC >> 2, yCg = yC >> 2;
        int xL = xC & 3, yL = yC & 3;
        int cgScan;
        int localScan;
        if (scanIdx == HevcScanTables.ScanDiag)
        {
            localScan = HevcScanTables.DiagScan4x4Inv[yL, xL];
            cgScan = trafoSize switch
            {
                4 => 0,
                8 => HevcScanTables.DiagScan2x2Inv[yCg, xCg],
                16 => HevcScanTables.DiagScan4x4Inv[yCg, xCg],
                _ => HevcScanTables.DiagScan8x8Inv[yCg, xCg],
            };
        }
        else if (scanIdx == HevcScanTables.ScanHoriz)
        {
            localScan = (yL * 4) + xL;
            cgScan = trafoSize == 4 ? 0 : (yCg * (trafoSize >> 2)) + xCg;
        }
        else
        {
            localScan = (xL * 4) + yL;
            cgScan = trafoSize == 4 ? 0 : (xCg * (trafoSize >> 2)) + yCg;
        }

        return (cgScan << 4) + localScan;
    }

    private static bool SubBlockHasCoeff(ReadOnlySpan<short> coeff, int trafoSize, int xCg, int yCg, byte[] scanXOff, byte[] scanYOff)
    {
        for (int n = 0; n < 16; n++)
        {
            int xC = (xCg * 4) + scanXOff[n];
            int yC = (yCg * 4) + scanYOff[n];
            if (xC < trafoSize && yC < trafoSize && coeff[(yC * trafoSize) + xC] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ComputeScanOrder(int scanIdx, int trafoSize, int lastX, int lastY, int xCgLastSig, int yCgLastSig,
        out byte[] scanXOff, out byte[] scanYOff, out byte[] scanXCg, out byte[] scanYCg, out int numCoeffInScanOrder)
    {
        if (scanIdx == HevcScanTables.ScanDiag)
        {
            int lastXLocal = lastX & 3, lastYLocal = lastY & 3;
            scanXOff = HevcScanTables.DiagScan4x4X;
            scanYOff = HevcScanTables.DiagScan4x4Y;
            numCoeffInScanOrder = HevcScanTables.DiagScan4x4Inv[lastYLocal, lastXLocal];
            if (trafoSize == 4)
            {
                scanXCg = new byte[] { 0 };
                scanYCg = new byte[] { 0 };
            }
            else if (trafoSize == 8)
            {
                numCoeffInScanOrder += HevcScanTables.DiagScan2x2Inv[yCgLastSig, xCgLastSig] << 4;
                scanXCg = HevcScanTables.DiagScan2x2X;
                scanYCg = HevcScanTables.DiagScan2x2Y;
            }
            else if (trafoSize == 16)
            {
                numCoeffInScanOrder += HevcScanTables.DiagScan4x4Inv[yCgLastSig, xCgLastSig] << 4;
                scanXCg = HevcScanTables.DiagScan4x4X;
                scanYCg = HevcScanTables.DiagScan4x4Y;
            }
            else
            {
                numCoeffInScanOrder += HevcScanTables.DiagScan8x8Inv[yCgLastSig, xCgLastSig] << 4;
                scanXCg = HevcScanTables.DiagScan8x8X;
                scanYCg = HevcScanTables.DiagScan8x8Y;
            }
        }
        else if (scanIdx == HevcScanTables.ScanHoriz)
        {
            scanXCg = HevcScanTables.HorizScan2x2X;
            scanYCg = HevcScanTables.HorizScan2x2Y;
            scanXOff = HevcScanTables.HorizScan4x4X;
            scanYOff = HevcScanTables.HorizScan4x4Y;
            if (trafoSize == 4)
            {
                numCoeffInScanOrder = (lastY * 4) + lastX;
            }
            else
            {
                int lastXLocal = lastX & 3, lastYLocal = lastY & 3;
                numCoeffInScanOrder = (((yCgLastSig * 2) + xCgLastSig) * 16) + ((lastYLocal * 4) + lastXLocal);
            }
        }
        else
        {
            scanXCg = HevcScanTables.HorizScan2x2Y;
            scanYCg = HevcScanTables.HorizScan2x2X;
            scanXOff = HevcScanTables.HorizScan4x4Y;
            scanYOff = HevcScanTables.HorizScan4x4X;
            if (trafoSize == 4)
            {
                numCoeffInScanOrder = (lastX * 4) + lastY;
            }
            else
            {
                int lastXLocal = lastX & 3, lastYLocal = lastY & 3;
                numCoeffInScanOrder = (((xCgLastSig * 2) + yCgLastSig) * 16) + ((lastXLocal * 4) + lastYLocal);
            }
        }
    }

    private static void EncodeLastSigPrefixValue(HevcCabacEncoder cabac, int log2TrafoSize, int cIdx, int value, bool isX, out int prefix)
    {
        int ctxOffset = isX ? HevcCabacContextIndex.LastSignificantCoeffXPrefix : HevcCabacContextIndex.LastSignificantCoeffYPrefix;
        int ctxShift;
        if (cIdx == 0)
        {
            ctxOffset += (3 * (log2TrafoSize - 2)) + ((log2TrafoSize - 1) >> 2);
            ctxShift = (log2TrafoSize + 1) >> 2;
        }
        else
        {
            ctxOffset += 15;
            ctxShift = log2TrafoSize - 2;
        }

        int maxPrefix = (log2TrafoSize << 1) - 1;
        prefix = ValueToLastPrefix(value);
        for (int p = 0; p < prefix; p++)
        {
            cabac.EncodeBin(ctxOffset + (p >> ctxShift), 1);
        }

        if (prefix < maxPrefix)
        {
            cabac.EncodeBin(ctxOffset + (prefix >> ctxShift), 0);
        }
    }

    private static void EncodeLastSigSuffix(HevcCabacEncoder cabac, int prefix, int value)
    {
        if (prefix >= 4)
        {
            int suffixBits = (prefix >> 1) - 1;
            int suffix = value - (((2 + (prefix & 1)) << suffixBits));
            cabac.EncodeBypassBins((uint)suffix, suffixBits);
        }
    }

    private static int ValueToLastPrefix(int value)
    {
        if (value < 4)
        {
            return value;
        }

        int group = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);
        return (2 * group) + ((value >> (group - 1)) & 1);
    }

    private static void WriteCoeffAbsLevelRemaining(HevcCabacEncoder cabac, int value, int rParam)
    {
        // Port of kvz_cabac_write_coeff_remain (matches the decoder's Golomb-Rice reader).
        if (value < (3 << rParam))
        {
            int length = value >> rParam;
            cabac.EncodeBypassBins((uint)((1 << (length + 1)) - 2), length + 1);
            cabac.EncodeBypassBins((uint)(value % (1 << rParam)), rParam);
        }
        else
        {
            int length = rParam;
            int codeNumber = value - (3 << rParam);
            while (codeNumber >= (1 << length))
            {
                codeNumber -= 1 << length;
                length++;
            }

            cabac.EncodeBypassBins((uint)((1 << (3 + length + 1 - rParam)) - 2), 3 + length + 1 - rParam);
            cabac.EncodeBypassBins((uint)codeNumber, length);
        }
    }
}
