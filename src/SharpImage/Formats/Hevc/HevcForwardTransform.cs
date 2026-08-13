// HEVC forward DCT/DST + forward quantization — the encoder-side mirror of HevcTransform's inverse
// path. Matrices and shifts ported from kvazaar (dct-generic.c, quant-generic.c, transform.c).
// The 1D transform is expressed as a matrix multiply (mathematically identical to the reference
// partial-butterfly, easier to audit): dst[k*n+j] = clip16((Σ_i M[k][i]·src[j*n+i] + add) >> shift).
using System;

namespace SharpImage.Formats.Hevc;

internal static class HevcForwardTransform
{
    private const int MaxTrDynamicRange = 15;
    private const int QuantShift = 14;

    // Flat forward quant scales (kvz_g_quant_scales); pairs with the decoder's inverse {40,45,51,57,64,72}.
    private static readonly int[] QuantScales = { 26214, 23302, 20560, 18396, 16384, 14564 };

    // Chroma QP mapping (kvz_g_chroma_scale, 58 entries).
    private static readonly byte[] ChromaScale =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30, 31, 32,
        33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44,
        45, 46, 47, 48, 49, 50, 51,
    };

    private static readonly short[] Dst4 =
    {
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    };

    private static readonly short[] Dct4 =
    {
        64, 64, 64, 64,
        83, 36, -36, -83,
        64, -64, -64, 64,
        36, -83, 83, -36,
    };

    private static readonly short[] Dct8 =
    {
        64, 64, 64, 64, 64, 64, 64, 64,
        89, 75, 50, 18, -18, -50, -75, -89,
        83, 36, -36, -83, -83, -36, 36, 83,
        75, -18, -89, -50, 50, 89, 18, -75,
        64, -64, -64, 64, 64, -64, -64, 64,
        50, -89, 18, 75, -75, -18, 89, -50,
        36, -83, 83, -36, -36, 83, -83, 36,
        18, -50, 75, -89, 89, -75, 50, -18,
    };

    private static readonly short[] Dct16 =
    {
        64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64,
        90, 87, 80, 70, 57, 43, 25, 9, -9, -25, -43, -57, -70, -80, -87, -90,
        89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89,
        87, 57, 9, -43, -80, -90, -70, -25, 25, 70, 90, 80, 43, -9, -57, -87,
        83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83,
        80, 9, -70, -87, -25, 57, 90, 43, -43, -90, -57, 25, 87, 70, -9, -80,
        75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75,
        70, -43, -87, 9, 90, 25, -80, -57, 57, 80, -25, -90, -9, 87, 43, -70,
        64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64,
        57, -80, -25, 90, -9, -87, 43, 70, -70, -43, 87, 9, -90, 25, 80, -57,
        50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50,
        43, -90, 57, 25, -87, 70, 9, -80, 80, -9, -70, 87, -25, -57, 90, -43,
        36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36,
        25, -70, 90, -80, 43, 9, -57, 87, -87, 57, -9, -43, 80, -90, 70, -25,
        18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18,
        9, -25, 43, -57, 70, -80, 87, -90, 90, -87, 80, -70, 57, -43, 25, -9,
    };

    private static readonly short[] Dct32 = BuildDct32();

    /// <summary>2D forward transform of a residual block (row-major, size×size). DST4 is used only for 4×4 intra luma.</summary>
    public static void Forward(ReadOnlySpan<short> residual, Span<short> coeff, int size, int bitDepth, bool useDst)
    {
        int log2Size = Log2(size);
        short[] matrix = useDst ? Dst4 : size switch { 4 => Dct4, 8 => Dct8, 16 => Dct16, _ => Dct32 };
        int shift1 = log2Size - 1 + (bitDepth - 8);
        int shift2 = log2Size + 6;

        Span<short> tmp = size <= 16 ? stackalloc short[16 * 16] : new short[32 * 32];
        Butterfly(residual, tmp, matrix, size, shift1);
        Butterfly(tmp, coeff, matrix, size, shift2);
    }

    // One 1D pass matching the reference butterfly's transposing I/O layout.
    private static void Butterfly(ReadOnlySpan<short> src, Span<short> dst, short[] m, int n, int shift)
    {
        int add = 1 << (shift - 1);
        for (int j = 0; j < n; j++)
        {
            int srcRow = j * n;
            for (int k = 0; k < n; k++)
            {
                int mrow = k * n;
                int sum = 0;
                for (int i = 0; i < n; i++)
                {
                    sum += m[mrow + i] * src[srcRow + i];
                }

                int v = (sum + add) >> shift;
                if (v < -32768)
                {
                    v = -32768;
                }
                else if (v > 32767)
                {
                    v = 32767;
                }

                dst[(k * n) + j] = (short)v;
            }
        }
    }

    /// <summary>Maps a base QP to the transform-domain QP (luma passes through; chroma uses the HEVC mapping).</summary>
    public static int GetScaledQp(bool chroma, int qp)
    {
        if (!chroma)
        {
            return qp;
        }

        int q = Math.Clamp(qp, 0, 57);
        return ChromaScale[q];
    }

    /// <summary>
    /// Forward quantization in place (flat scaling list). qpScaled from <see cref="GetScaledQp"/>.
    /// When <paramref name="deltaU"/> is provided it receives the per-coefficient rounding remainder
    /// (used by sign-data-hiding).
    /// </summary>
    public static void Quantize(Span<short> coeff, int qpScaled, int size, int bitDepth, bool intra, Span<int> deltaU = default)
    {
        int log2Size = Log2(size);
        int transformShift = MaxTrDynamicRange - bitDepth - log2Size;
        int qBits = QuantShift + (qpScaled / 6) + transformShift;
        int qBits8 = qBits - 8;
        int scale = QuantScales[qpScaled % 6];
        int add = (intra ? 171 : 85) << (qBits - 9);
        bool wantDelta = deltaU.Length == size * size;

        int count = size * size;
        for (int i = 0; i < count; i++)
        {
            int level = coeff[i];
            int sign = level < 0 ? -1 : 1;
            long absLevel = Math.Abs((long)level);
            long q = ((absLevel * scale) + add) >> qBits;
            if (wantDelta)
            {
                deltaU[i] = (int)(((absLevel * scale) - (q << qBits)) >> qBits8);
            }

            q *= sign;
            coeff[i] = (short)Math.Clamp(q, -32768, 32767);
        }
    }

    /// <summary>
    /// Sign-data-hiding coefficient adjustment (HEVC encoder side, ported from kvazaar). For each 4×4
    /// coefficient group whose last-first significant span is ≥4, forces the abs-sum parity to encode
    /// the (uncoded) sign of the first significant coefficient, adjusting the min-cost coefficient by ±1.
    /// </summary>
    public static void ApplySignHiding(Span<short> qCoef, ReadOnlySpan<short> origCoef, ReadOnlySpan<int> deltaU, int size, int scanIdx)
    {
        int total = size * size;
        int[] scan = BuildScanOrder(size, scanIdx); // scan index -> raster position
        for (int subset = (total - 1) >> 4; subset >= 0; subset--)
        {
            int subpos = subset << 4;
            int firstNz = 16, lastNz = -1, absSum = 0;
            for (int n = 15; n >= 0; n--)
            {
                if (qCoef[scan[n + subpos]] != 0) { lastNz = n; break; }
            }

            for (int n = 0; n < 16; n++)
            {
                if (qCoef[scan[n + subpos]] != 0) { firstNz = n; break; }
            }

            for (int n = firstNz; n <= lastNz; n++)
            {
                absSum += qCoef[scan[n + subpos]];
            }

            if (lastNz - firstNz < 4)
            {
                continue;
            }

            int signbit = qCoef[scan[subpos + firstNz]] > 0 ? 0 : 1;
            if (signbit == (absSum & 1))
            {
                continue;
            }

            int minCost = int.MaxValue, minPos = -1;
            int finalChange = 0;
            for (int n = lastNz; n >= 0; n--)
            {
                int blkPos = scan[n + subpos];
                int curCost;
                int curChange = 0;
                if (qCoef[blkPos] != 0)
                {
                    if (deltaU[blkPos] > 0)
                    {
                        curCost = -deltaU[blkPos];
                        curChange = 1;
                    }
                    else if (n == firstNz && Math.Abs(qCoef[blkPos]) == 1)
                    {
                        curCost = int.MaxValue;
                    }
                    else
                    {
                        curCost = deltaU[blkPos];
                        curChange = -1;
                    }
                }
                else if (n < firstNz && (origCoef[blkPos] >= 0 ? 0 : 1) != signbit)
                {
                    curCost = int.MaxValue;
                }
                else
                {
                    curCost = -deltaU[blkPos];
                    curChange = 1;
                }

                if (curCost < minCost)
                {
                    minCost = curCost;
                    finalChange = curChange;
                    minPos = blkPos;
                }
            }

            if (minPos < 0)
            {
                continue;
            }

            if (qCoef[minPos] == 32767 || qCoef[minPos] == -32768)
            {
                finalChange = -1;
            }

            if (origCoef[minPos] >= 0)
            {
                qCoef[minPos] = (short)(qCoef[minPos] + finalChange);
            }
            else
            {
                qCoef[minPos] = (short)(qCoef[minPos] - finalChange);
            }
        }
    }

    private static int[] BuildScanOrder(int size, int scanIdx)
    {
        int total = size * size;
        var scan = new int[total];
        int cgCount = Math.Max(1, (size >> 2) * (size >> 2));
        byte[] scanXCg, scanYCg, scanXOff, scanYOff;
        if (scanIdx == HevcScanTables.ScanDiag)
        {
            scanXOff = HevcScanTables.DiagScan4x4X; scanYOff = HevcScanTables.DiagScan4x4Y;
            (scanXCg, scanYCg) = size switch
            {
                4 => (new byte[] { 0 }, new byte[] { 0 }),
                8 => (HevcScanTables.DiagScan2x2X, HevcScanTables.DiagScan2x2Y),
                16 => (HevcScanTables.DiagScan4x4X, HevcScanTables.DiagScan4x4Y),
                _ => (HevcScanTables.DiagScan8x8X, HevcScanTables.DiagScan8x8Y),
            };
        }
        else if (scanIdx == HevcScanTables.ScanHoriz)
        {
            scanXOff = HevcScanTables.HorizScan4x4X; scanYOff = HevcScanTables.HorizScan4x4Y;
            scanXCg = HevcScanTables.HorizScan2x2X; scanYCg = HevcScanTables.HorizScan2x2Y;
        }
        else
        {
            scanXOff = HevcScanTables.HorizScan4x4Y; scanYOff = HevcScanTables.HorizScan4x4X;
            scanXCg = HevcScanTables.HorizScan2x2Y; scanYCg = HevcScanTables.HorizScan2x2X;
        }

        for (int cg = 0; cg < cgCount; cg++)
        {
            int xCg = size == 4 ? 0 : scanXCg[cg];
            int yCg = size == 4 ? 0 : scanYCg[cg];
            for (int n = 0; n < 16; n++)
            {
                int xC = (xCg * 4) + scanXOff[n];
                int yC = (yCg * 4) + scanYOff[n];
                scan[(cg << 4) + n] = (yC * size) + xC;
            }
        }

        return scan;
    }

    private static int Log2(int v) => v switch { 4 => 2, 8 => 3, 16 => 4, 32 => 5, _ => 2 };

    // The 4/8/16-point matrices are the even-symmetric subsets of the 32-point matrix, but HEVC
    // defines each explicitly; the 32-point one is reproduced from the spec basis.
    private static short[] BuildDct32()
    {
        // Row 0 is all-64; row k[n] = round(64*sqrt(2/32) style integer basis). Reproduced verbatim.
        int[][] rows =
        {
            R(64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64),
            R(90, 90, 88, 85, 82, 78, 73, 67, 61, 54, 46, 38, 31, 22, 13, 4, -4, -13, -22, -31, -38, -46, -54, -61, -67, -73, -78, -82, -85, -88, -90, -90),
            R(90, 87, 80, 70, 57, 43, 25, 9, -9, -25, -43, -57, -70, -80, -87, -90, -90, -87, -80, -70, -57, -43, -25, -9, 9, 25, 43, 57, 70, 80, 87, 90),
            R(90, 82, 67, 46, 22, -4, -31, -54, -73, -85, -90, -88, -78, -61, -38, -13, 13, 38, 61, 78, 88, 90, 85, 73, 54, 31, 4, -22, -46, -67, -82, -90),
            R(89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89, 89, 75, 50, 18, -18, -50, -75, -89, -89, -75, -50, -18, 18, 50, 75, 89),
            R(88, 67, 31, -13, -54, -82, -90, -78, -46, -4, 38, 73, 90, 85, 61, 22, -22, -61, -85, -90, -73, -38, 4, 46, 78, 90, 82, 54, 13, -31, -67, -88),
            R(87, 57, 9, -43, -80, -90, -70, -25, 25, 70, 90, 80, 43, -9, -57, -87, -87, -57, -9, 43, 80, 90, 70, 25, -25, -70, -90, -80, -43, 9, 57, 87),
            R(85, 46, -13, -67, -90, -73, -22, 38, 82, 88, 54, -4, -61, -90, -78, -31, 31, 78, 90, 61, 4, -54, -88, -82, -38, 22, 73, 90, 67, 13, -46, -85),
            R(83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83, 83, 36, -36, -83, -83, -36, 36, 83),
            R(82, 22, -54, -90, -61, 13, 78, 85, 31, -46, -90, -67, 4, 73, 88, 38, -38, -88, -73, -4, 67, 90, 46, -31, -85, -78, -13, 61, 90, 54, -22, -82),
            R(80, 9, -70, -87, -25, 57, 90, 43, -43, -90, -57, 25, 87, 70, -9, -80, -80, -9, 70, 87, 25, -57, -90, -43, 43, 90, 57, -25, -87, -70, 9, 80),
            R(78, -4, -82, -73, 13, 85, 67, -22, -88, -61, 31, 90, 54, -38, -90, -46, 46, 90, 38, -54, -90, -31, 61, 88, 22, -67, -85, -13, 73, 82, 4, -78),
            R(75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75, 75, -18, -89, -50, 50, 89, 18, -75, -75, 18, 89, 50, -50, -89, -18, 75),
            R(73, -31, -90, -22, 78, 67, -38, -90, -13, 82, 61, -46, -88, -4, 85, 54, -54, -85, 4, 88, 46, -61, -82, 13, 90, 38, -67, -78, 22, 90, 31, -73),
            R(70, -43, -87, 9, 90, 25, -80, -57, 57, 80, -25, -90, -9, 87, 43, -70, -70, 43, 87, -9, -90, -25, 80, 57, -57, -80, 25, 90, 9, -87, -43, 70),
            R(67, -54, -78, 38, 85, -22, -90, 4, 90, 13, -88, -31, 82, 46, -73, -61, 61, 73, -46, -82, 31, 88, -13, -90, -4, 90, 22, -85, -38, 78, 54, -67),
            R(64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64, 64, -64, -64, 64),
            R(61, -73, -46, 82, 31, -88, -13, 90, -4, -90, 22, 85, -38, -78, 54, 67, -67, -54, 78, 38, -85, -22, 90, 4, -90, 13, 88, -31, -82, 46, 73, -61),
            R(57, -80, -25, 90, -9, -87, 43, 70, -70, -43, 87, 9, -90, 25, 80, -57, -57, 80, 25, -90, 9, 87, -43, -70, 70, 43, -87, -9, 90, -25, -80, 57),
            R(54, -85, -4, 88, -46, -61, 82, 13, -90, 38, 67, -78, -22, 90, -31, -73, 73, 31, -90, 22, 78, -67, -38, 90, -13, -82, 61, 46, -88, 4, 85, -54),
            R(50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50, 50, -89, 18, 75, -75, -18, 89, -50, -50, 89, -18, -75, 75, 18, -89, 50),
            R(46, -90, 38, 54, -90, 31, 61, -88, 22, 67, -85, 13, 73, -82, 4, 78, -78, -4, 82, -73, -13, 85, -67, -22, 88, -61, -31, 90, -54, -38, 90, -46),
            R(43, -90, 57, 25, -87, 70, 9, -80, 80, -9, -70, 87, -25, -57, 90, -43, -43, 90, -57, -25, 87, -70, -9, 80, -80, 9, 70, -87, 25, 57, -90, 43),
            R(38, -88, 73, -4, -67, 90, -46, -31, 85, -78, 13, 61, -90, 54, 22, -82, 82, -22, -54, 90, -61, -13, 78, -85, 31, 46, -90, 67, 4, -73, 88, -38),
            R(36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36, 36, -83, 83, -36, -36, 83, -83, 36),
            R(31, -78, 90, -61, 4, 54, -88, 82, -38, -22, 73, -90, 67, -13, -46, 85, -85, 46, 13, -67, 90, -73, 22, 38, -82, 88, -54, -4, 61, -90, 78, -31),
            R(25, -70, 90, -80, 43, 9, -57, 87, -87, 57, -9, -43, 80, -90, 70, -25, -25, 70, -90, 80, -43, -9, 57, -87, 87, -57, 9, 43, -80, 90, -70, 25),
            R(22, -61, 85, -90, 73, -38, -4, 46, -78, 90, -82, 54, -13, -31, 67, -88, 88, -67, 31, 13, -54, 82, -90, 78, -46, 4, 38, -73, 90, -85, 61, -22),
            R(18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18, 18, -50, 75, -89, 89, -75, 50, -18, -18, 50, -75, 89, -89, 75, -50, 18),
            R(13, -38, 61, -78, 88, -90, 85, -73, 54, -31, 4, 22, -46, 67, -82, 90, -90, 82, -67, 46, -22, -4, 31, -54, 73, -85, 90, -88, 78, -61, 38, -13),
            R(9, -25, 43, -57, 70, -80, 87, -90, 90, -87, 80, -70, 57, -43, 25, -9, -9, 25, -43, 57, -70, 80, -87, 90, -90, 87, -80, 70, -57, 43, -25, 9),
            R(4, -13, 22, -31, 38, -46, 54, -61, 67, -73, 78, -82, 85, -88, 90, -90, 90, -90, 88, -85, 82, -78, 73, -67, 61, -54, 46, -38, 31, -22, 13, -4),
        };

        var flat = new short[32 * 32];
        for (int k = 0; k < 32; k++)
        {
            for (int n = 0; n < 32; n++)
            {
                flat[(k * 32) + n] = (short)rows[k][n];
            }
        }

        return flat;
    }

    private static int[] R(params int[] v) => v;
}
