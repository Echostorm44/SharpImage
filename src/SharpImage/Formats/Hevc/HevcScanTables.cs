// HEVC coefficient scan orders + sig_coeff_flag context map, shared by the residual encoder.
// Values match the decoder's tables (HevcDecoder.ResidualCoding); the residual round-trip test
// guarantees the two agree.
namespace SharpImage.Formats.Hevc;

internal static class HevcScanTables
{
    public const int ScanDiag = 0;
    public const int ScanHoriz = 1;
    public const int ScanVert = 2;

    public static readonly byte[] DiagScan4x4X =
        { 0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 1, 2, 3, 2, 3, 3 };
    public static readonly byte[] DiagScan4x4Y =
        { 0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 3, 2, 1, 3, 2, 3 };
    public static readonly byte[,] DiagScan4x4Inv = new byte[4, 4]
    {
        { 0, 2, 5, 9 },
        { 1, 4, 8, 12 },
        { 3, 7, 11, 14 },
        { 6, 10, 13, 15 },
    };

    public static readonly byte[] DiagScan2x2X = { 0, 0, 1, 1 };
    public static readonly byte[] DiagScan2x2Y = { 0, 1, 0, 1 };
    public static readonly byte[,] DiagScan2x2Inv = new byte[2, 2]
    {
        { 0, 2 },
        { 1, 3 },
    };

    public static readonly byte[] DiagScan8x8X =
    {
        0, 0, 1, 0, 1, 2, 0, 1, 2, 3, 0, 1, 2, 3, 4, 0,
        1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 6, 0, 1, 2, 3,
        4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 2, 3, 4, 5, 6,
        7, 3, 4, 5, 6, 7, 4, 5, 6, 7, 5, 6, 7, 6, 7, 7,
    };
    public static readonly byte[] DiagScan8x8Y =
    {
        0, 1, 0, 2, 1, 0, 3, 2, 1, 0, 4, 3, 2, 1, 0, 5,
        4, 3, 2, 1, 0, 6, 5, 4, 3, 2, 1, 0, 7, 6, 5, 4,
        3, 2, 1, 0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3,
        2, 7, 6, 5, 4, 3, 7, 6, 5, 4, 7, 6, 5, 7, 6, 7,
    };
    public static readonly byte[,] DiagScan8x8Inv = new byte[8, 8]
    {
        { 0, 2, 5, 9, 14, 20, 27, 35 },
        { 1, 4, 8, 13, 19, 26, 34, 42 },
        { 3, 7, 12, 18, 25, 33, 41, 48 },
        { 6, 11, 17, 24, 32, 40, 47, 53 },
        { 10, 16, 23, 31, 39, 46, 52, 57 },
        { 15, 22, 30, 38, 45, 51, 56, 60 },
        { 21, 29, 37, 44, 50, 55, 59, 62 },
        { 28, 36, 43, 49, 54, 58, 61, 63 },
    };

    public static readonly byte[] HorizScan4x4X =
        { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 };
    public static readonly byte[] HorizScan4x4Y =
        { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3 };
    public static readonly byte[] HorizScan2x2X = { 0, 1, 0, 1 };
    public static readonly byte[] HorizScan2x2Y = { 0, 0, 1, 1 };

    public static readonly byte[] SigCoeffCtxIdxMap =
    {
        0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8, // log2TrafoSize == 2
        1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, // prev_sig == 0
        2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, // prev_sig == 1
        2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0, 2, 1, 0, 0, // prev_sig == 2
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // default (prev_sig == 3)
    };
}
