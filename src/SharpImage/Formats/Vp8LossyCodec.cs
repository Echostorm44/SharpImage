using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// VP8 lossy codec for WebP. Implements a keyframe-only decoder following RFC 6386 and a simplified encoder using DC
/// prediction with boolean arithmetic coding.
/// </summary>
internal static class Vp8LossyCodec
{
    // Coefficient band mapping: position 0-15 → band 0-7
    private static readonly int[] Bands = [ 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7 ];

    // 4x4 DCT zigzag scan order
    private static readonly int[] ZigZag = [ 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 ];

    // DC/AC dequantization lookup tables (128 entries each, from RFC 6386 Section 14.1)
    private static readonly int[] DcQLookup =[ 4, 5, 6, 7, 8, 9, 10, 10, 11, 12, 13, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 25, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 91, 93, 95, 96, 98, 100, 101, 102, 104, 106, 108, 110, 112, 114, 116, 118, 122, 124, 126, 128, 130, 132, 134, 136, 138, 140, 143, 145, 148, 151, 154, 157 ];
    private static readonly int[] AcQLookup =[ 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76, 78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152, 155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284 ];

    // Per-macroblock working-buffer scanline stride (bytes) used by the intra predictors.
    private const int Bps = 32;

    // Keyframe 4x4 intra submode probabilities, kBModesProba[above][left][9] from RFC 6386
    // §11.5, flattened to (above*10 + left)*9 + node. Selects the B_PRED submode tree.
    private static readonly byte[] KBModesProba =
    [
        231, 120, 48, 89, 115, 113, 120, 152, 112,
        152, 179, 64, 126, 170, 118, 46, 70, 95,
        175, 69, 143, 80, 85, 82, 72, 155, 103,
        56, 58, 10, 171, 218, 189, 17, 13, 152,
        114, 26, 17, 163, 44, 195, 21, 10, 173,
        121, 24, 80, 195, 26, 62, 44, 64, 85,
        144, 71, 10, 38, 171, 213, 144, 34, 26,
        170, 46, 55, 19, 136, 160, 33, 206, 71,
        63, 20, 8, 114, 114, 208, 12, 9, 226,
        81, 40, 11, 96, 182, 84, 29, 16, 36,
        134, 183, 89, 137, 98, 101, 106, 165, 148,
        72, 187, 100, 130, 157, 111, 32, 75, 80,
        66, 102, 167, 99, 74, 62, 40, 234, 128,
        41, 53, 9, 178, 241, 141, 26, 8, 107,
        74, 43, 26, 146, 73, 166, 49, 23, 157,
        65, 38, 105, 160, 51, 52, 31, 115, 128,
        104, 79, 12, 27, 217, 255, 87, 17, 7,
        87, 68, 71, 44, 114, 51, 15, 186, 23,
        47, 41, 14, 110, 182, 183, 21, 17, 194,
        66, 45, 25, 102, 197, 189, 23, 18, 22,
        88, 88, 147, 150, 42, 46, 45, 196, 205,
        43, 97, 183, 117, 85, 38, 35, 179, 61,
        39, 53, 200, 87, 26, 21, 43, 232, 171,
        56, 34, 51, 104, 114, 102, 29, 93, 77,
        39, 28, 85, 171, 58, 165, 90, 98, 64,
        34, 22, 116, 206, 23, 34, 43, 166, 73,
        107, 54, 32, 26, 51, 1, 81, 43, 31,
        68, 25, 106, 22, 64, 171, 36, 225, 114,
        34, 19, 21, 102, 132, 188, 16, 76, 124,
        62, 18, 78, 95, 85, 57, 50, 48, 51,
        193, 101, 35, 159, 215, 111, 89, 46, 111,
        60, 148, 31, 172, 219, 228, 21, 18, 111,
        112, 113, 77, 85, 179, 255, 38, 120, 114,
        40, 42, 1, 196, 245, 209, 10, 25, 109,
        88, 43, 29, 140, 166, 213, 37, 43, 154,
        61, 63, 30, 155, 67, 45, 68, 1, 209,
        100, 80, 8, 43, 154, 1, 51, 26, 71,
        142, 78, 78, 16, 255, 128, 34, 197, 171,
        41, 40, 5, 102, 211, 183, 4, 1, 221,
        51, 50, 17, 168, 209, 192, 23, 25, 82,
        138, 31, 36, 171, 27, 166, 38, 44, 229,
        67, 87, 58, 169, 82, 115, 26, 59, 179,
        63, 59, 90, 180, 59, 166, 93, 73, 154,
        40, 40, 21, 116, 143, 209, 34, 39, 175,
        47, 15, 16, 183, 34, 223, 49, 45, 183,
        46, 17, 33, 183, 6, 98, 15, 32, 183,
        57, 46, 22, 24, 128, 1, 54, 17, 37,
        65, 32, 73, 115, 28, 128, 23, 128, 205,
        40, 3, 9, 115, 51, 192, 18, 6, 223,
        87, 37, 9, 115, 59, 77, 64, 21, 47,
        104, 55, 44, 218, 9, 54, 53, 130, 226,
        64, 90, 70, 205, 40, 41, 23, 26, 57,
        54, 57, 112, 184, 5, 41, 38, 166, 213,
        30, 34, 26, 133, 152, 116, 10, 32, 134,
        39, 19, 53, 221, 26, 114, 32, 73, 255,
        31, 9, 65, 234, 2, 15, 1, 118, 73,
        75, 32, 12, 51, 192, 255, 160, 43, 51,
        88, 31, 35, 67, 102, 85, 55, 186, 85,
        56, 21, 23, 111, 59, 205, 45, 37, 192,
        55, 38, 70, 124, 73, 102, 1, 34, 98,
        125, 98, 42, 88, 104, 85, 117, 175, 82,
        95, 84, 53, 89, 128, 100, 113, 101, 45,
        75, 79, 123, 47, 51, 128, 81, 171, 1,
        57, 17, 5, 71, 102, 57, 53, 41, 49,
        38, 33, 13, 121, 57, 73, 26, 1, 85,
        41, 10, 67, 138, 77, 110, 90, 47, 114,
        115, 21, 2, 10, 102, 255, 166, 23, 6,
        101, 29, 16, 10, 85, 128, 101, 196, 26,
        57, 18, 10, 102, 102, 213, 34, 20, 43,
        117, 20, 15, 36, 163, 128, 68, 1, 26,
        102, 61, 71, 37, 34, 53, 31, 243, 192,
        69, 60, 71, 38, 73, 119, 28, 222, 37,
        68, 45, 128, 34, 1, 47, 11, 245, 171,
        62, 17, 19, 70, 146, 85, 55, 62, 70,
        37, 43, 37, 154, 100, 163, 85, 160, 1,
        63, 9, 92, 136, 28, 64, 32, 201, 85,
        75, 15, 9, 9, 64, 255, 184, 119, 16,
        86, 6, 28, 5, 64, 255, 25, 248, 1,
        56, 8, 17, 132, 137, 255, 55, 116, 128,
        58, 15, 20, 82, 135, 57, 26, 121, 40,
        164, 50, 31, 137, 154, 133, 25, 35, 218,
        51, 103, 44, 131, 131, 123, 31, 6, 158,
        86, 40, 64, 135, 148, 224, 45, 183, 128,
        22, 26, 17, 131, 240, 154, 14, 1, 209,
        45, 16, 21, 91, 64, 222, 7, 1, 197,
        56, 21, 39, 155, 60, 138, 23, 102, 213,
        83, 12, 13, 54, 192, 255, 68, 47, 28,
        85, 26, 85, 85, 128, 128, 32, 146, 171,
        18, 11, 7, 63, 144, 171, 4, 4, 246,
        35, 27, 10, 146, 174, 171, 12, 26, 128,
        190, 80, 35, 99, 180, 80, 126, 54, 45,
        85, 126, 47, 87, 176, 51, 41, 20, 32,
        101, 75, 128, 139, 118, 146, 116, 128, 85,
        56, 41, 15, 176, 236, 85, 37, 9, 62,
        71, 30, 17, 119, 118, 255, 17, 18, 138,
        101, 38, 60, 138, 55, 70, 43, 26, 142,
        146, 36, 19, 30, 171, 255, 97, 27, 20,
        138, 45, 61, 62, 219, 1, 81, 188, 64,
        32, 41, 20, 117, 151, 142, 20, 21, 163,
        112, 19, 12, 61, 195, 128, 48, 4, 24,
    ];

    // Default coefficient probability table [4][8][3][11] from RFC 6386 Section 13.5
    // Flattened: index = type*264 + band*33 + ctx*11 + node
    private static readonly byte[] DefaultCoeffProbs =[
 // Type 0
 // Band 0
 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
 // Band 1
 253, 136, 254, 255, 228, 219, 128, 128, 128, 128, 128, 189, 129, 242, 255, 227, 213, 255, 219, 128, 128, 128, 106, 126, 227, 252, 214, 209, 255, 255, 128, 128, 128,
 // Band 2
 1, 98, 248, 255, 236, 226, 255, 255, 128, 128, 128, 181, 133, 238, 254, 221, 234, 255, 154, 128, 128, 128, 78, 134, 202, 247, 198, 180, 255, 219, 128, 128, 128,
 // Band 3
 1, 185, 249, 255, 243, 255, 128, 128, 128, 128, 128, 184, 150, 247, 255, 236, 224, 128, 128, 128, 128, 128, 77, 110, 216, 255, 236, 230, 128, 128, 128, 128, 128,
 // Band 4
 1, 101, 251, 255, 241, 255, 128, 128, 128, 128, 128, 170, 139, 241, 252, 236, 209, 255, 255, 128, 128, 128, 37, 116, 196, 243, 228, 255, 255, 255, 128, 128, 128,
 // Band 5
 1, 204, 254, 255, 245, 255, 128, 128, 128, 128, 128, 207, 160, 250, 255, 238, 128, 128, 128, 128, 128, 128, 102, 103, 231, 255, 211, 171, 128, 128, 128, 128, 128,
 // Band 6
 1, 152, 252, 255, 240, 255, 128, 128, 128, 128, 128, 177, 135, 243, 255, 234, 225, 128, 128, 128, 128, 128, 80, 129, 211, 255, 194, 224, 128, 128, 128, 128, 128,
 // Band 7
 1, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 246, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 255, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
 // Type 1
 // Band 0
 198, 35, 237, 223, 193, 187, 162, 160, 145, 155, 62, 131, 45, 198, 221, 172, 176, 220, 157, 252, 221, 1, 68, 47, 146, 208, 149, 167, 221, 162, 255, 223, 128,
 // Band 1
 1, 149, 241, 255, 221, 224, 255, 255, 128, 128, 128, 184, 141, 234, 253, 222, 220, 255, 199, 128, 128, 128, 81, 99, 181, 242, 176, 190, 249, 202, 255, 255, 128,
 // Band 2
 1, 129, 232, 253, 214, 197, 242, 196, 255, 255, 128, 99, 121, 210, 250, 201, 198, 255, 202, 128, 128, 128, 23, 91, 163, 242, 170, 187, 247, 210, 255, 255, 128,
 // Band 3
 1, 200, 246, 255, 234, 255, 128, 128, 128, 128, 128, 109, 178, 241, 255, 231, 245, 255, 255, 128, 128, 128, 44, 130, 201, 253, 205, 192, 255, 255, 128, 128, 128,
 // Band 4
 1, 132, 239, 251, 219, 209, 255, 165, 128, 128, 128, 94, 136, 225, 251, 218, 190, 255, 255, 128, 128, 128, 22, 100, 174, 245, 186, 161, 255, 199, 128, 128, 128,
 // Band 5
 1, 182, 249, 255, 232, 235, 128, 128, 128, 128, 128, 124, 143, 241, 255, 227, 234, 128, 128, 128, 128, 128, 35, 77, 181, 251, 193, 211, 255, 205, 128, 128, 128,
 // Band 6
 1, 157, 247, 255, 236, 231, 255, 255, 128, 128, 128, 121, 141, 235, 255, 225, 227, 255, 255, 128, 128, 128, 45, 99, 188, 251, 195, 217, 255, 224, 128, 128, 128,
 // Band 7
 1, 1, 251, 255, 213, 255, 128, 128, 128, 128, 128, 203, 1, 248, 255, 255, 128, 128, 128, 128, 128, 128, 137, 1, 177, 255, 224, 255, 128, 128, 128, 128, 128,
 // Type 2
 // Band 0
 253, 9, 248, 251, 207, 208, 255, 192, 128, 128, 128, 175, 13, 224, 243, 193, 185, 249, 198, 255, 255, 128, 73, 17, 171, 221, 161, 179, 236, 167, 255, 234, 128,
 // Band 1
 1, 95, 247, 253, 212, 183, 255, 255, 128, 128, 128, 239, 90, 244, 250, 211, 209, 255, 255, 128, 128, 128, 155, 77, 195, 248, 188, 195, 255, 255, 128, 128, 128,
 // Band 2
 1, 24, 239, 251, 218, 219, 255, 205, 128, 128, 128, 201, 51, 219, 255, 196, 186, 128, 128, 128, 128, 128, 69, 46, 190, 239, 201, 218, 255, 228, 128, 128, 128,
 // Band 3
 1, 191, 251, 255, 255, 128, 128, 128, 128, 128, 128, 223, 165, 249, 255, 213, 255, 128, 128, 128, 128, 128, 141, 124, 248, 255, 255, 128, 128, 128, 128, 128, 128,
 // Band 4
 1, 16, 248, 255, 255, 128, 128, 128, 128, 128, 128, 190, 36, 230, 255, 236, 255, 128, 128, 128, 128, 128, 149, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128,
 // Band 5
 1, 226, 255, 128, 128, 128, 128, 128, 128, 128, 128, 247, 192, 255, 128, 128, 128, 128, 128, 128, 128, 128, 240, 128, 255, 128, 128, 128, 128, 128, 128, 128, 128,
 // Band 6
 1, 134, 252, 255, 255, 128, 128, 128, 128, 128, 128, 213, 62, 250, 255, 255, 128, 128, 128, 128, 128, 128, 55, 93, 255, 128, 128, 128, 128, 128, 128, 128, 128,
 // Band 7
 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128, 128,
 // Type 3
 // Band 0
 202, 24, 213, 235, 186, 191, 220, 160, 240, 175, 255, 126, 38, 182, 232, 169, 184, 228, 174, 255, 187, 128, 61, 46, 138, 219, 151, 178, 240, 170, 255, 216, 128,
 // Band 1
 1, 112, 230, 250, 199, 191, 247, 159, 255, 255, 128, 166, 109, 228, 252, 211, 215, 255, 174, 128, 128, 128, 39, 77, 162, 232, 172, 180, 245, 178, 255, 255, 128,
 // Band 2
 1, 52, 220, 246, 198, 199, 249, 220, 255, 255, 128, 124, 74, 191, 243, 183, 193, 250, 221, 255, 255, 128, 24, 71, 130, 219, 154, 170, 243, 182, 255, 255, 128,
 // Band 3
 1, 182, 225, 249, 219, 240, 255, 224, 128, 128, 128, 149, 150, 226, 252, 216, 205, 255, 171, 128, 128, 128, 28, 108, 170, 242, 183, 194, 254, 223, 255, 255, 128,
 // Band 4
 1, 81, 230, 252, 204, 203, 255, 192, 128, 128, 128, 123, 102, 209, 247, 188, 196, 255, 233, 128, 128, 128, 20, 95, 153, 243, 164, 173, 255, 203, 128, 128, 128,
 // Band 5
 1, 222, 248, 255, 216, 213, 128, 128, 128, 128, 128, 168, 175, 246, 252, 235, 205, 255, 255, 128, 128, 128, 47, 116, 215, 255, 211, 212, 255, 255, 128, 128, 128,
 // Band 6
 1, 121, 236, 253, 212, 214, 255, 255, 128, 128, 128, 141, 84, 213, 252, 201, 202, 255, 219, 128, 128, 128, 42, 80, 160, 240, 162, 185, 255, 205, 128, 128, 128,
 // Band 7
 1, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 244, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, 238, 1, 255, 128, 128, 128, 128, 128, 128, 128, 128, ];

    // Coefficient update probabilities [4][8][3][11] from RFC 6386 Section 13.4
    private static readonly byte[] CoeffUpdateProbs =[
 // Type 0
 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 176, 246, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 241, 252, 255, 255, 255, 255, 255, 255, 255, 255, 249, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 244, 252, 255, 255, 255, 255, 255, 255, 255, 255, 234, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 246, 254, 255, 255, 255, 255, 255, 255, 255, 255, 239, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 253, 255, 254, 255, 255, 255, 255, 255, 255, 250, 255, 254, 255, 254, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
 // Type 1
 217, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 225, 252, 241, 253, 255, 255, 254, 255, 255, 255, 255, 234, 250, 241, 250, 253, 255, 253, 254, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 223, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 238, 253, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 248, 254, 255, 255, 255, 255, 255, 255, 255, 255, 249, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 247, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 252, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
 // Type 2
 186, 251, 250, 255, 255, 255, 255, 255, 255, 255, 255, 234, 251, 244, 254, 255, 255, 255, 255, 255, 255, 255, 251, 251, 243, 253, 254, 255, 254, 255, 255, 255, 255, 255, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 236, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 253, 253, 254, 254, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
 // Type 3
 248, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 250, 254, 252, 254, 255, 255, 255, 255, 255, 255, 255, 248, 254, 249, 253, 255, 255, 255, 255, 255, 255, 255, 255, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 246, 253, 253, 255, 255, 255, 255, 255, 255, 255, 255, 252, 254, 251, 254, 254, 255, 255, 255, 255, 255, 255, 255, 254, 252, 255, 255, 255, 255, 255, 255, 255, 255, 248, 254, 253, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 251, 254, 255, 255, 255, 255, 255, 255, 255, 255, 245, 251, 254, 255, 255, 255, 255, 255, 255, 255, 255, 253, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 251, 253, 255, 255, 255, 255, 255, 255, 255, 255, 252, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 252, 255, 255, 255, 255, 255, 255, 255, 255, 255, 249, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 253, 255, 255, 255, 255, 255, 255, 255, 255, 250, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, ];

    // ═══════════════════════════════════════════════════════════════════
    // VP8 Boolean Decoder (arithmetic coder from RFC 6386 Section 7)
    // ═══════════════════════════════════════════════════════════════════

    private struct BoolDecoder
    {
        private byte[] data;
        private int offset;
        private uint range;
        private uint value;
        private int count;

        public void Init(byte[] buf, int start)
        {
            data = buf;
            offset = start + 2;
            range = 255;
            value = (uint)((buf[start] << 8) | buf[start + 1]);
            count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadBool(int probability)
        {
            uint split = 1 + (((range - 1) * (uint)probability) >> 8);
            uint bigSplit = split << 8;
            int result;

            if (value >= bigSplit)
            {
                range -= split;
                value -= bigSplit;
                result = 1;
            }
            else
            {
                range = split;
                result = 0;
            }

            while (range < 128)
            {
                value <<= 1;
                range <<= 1;
                if (++count == 8)
                {
                    count = 0;
                    if (offset < data.Length)
                    {
                        value |= data[offset++];
                    }
                }
            }
            return result;
        }

        public int ReadLiteral(int bits)
        {
            int result = 0;
            for (int i = bits - 1;i >= 0;i--)
            {
                result |= ReadBool(128) << i;
            }

            return result;
        }

        public int ReadSignedLiteral(int bits)
        {
            int value = ReadLiteral(bits);
            return ReadBool(128) != 0 ? -value : value;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8 Boolean Encoder
    // ═══════════════════════════════════════════════════════════════════

    // Renormalisation tables for the arithmetic encoder (libwebp bit_writer_utils.c).
    private static readonly byte[] KNorm =
    [
        7, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
    ];

    private static readonly byte[] KNewRange =
    [
        127, 127, 191, 127, 159, 191, 223, 127, 143, 159, 175, 191, 207, 223, 239,
        127, 135, 143, 151, 159, 167, 175, 183, 191, 199, 207, 215, 223, 231, 239,
        247, 127, 131, 135, 139, 143, 147, 151, 155, 159, 163, 167, 171, 175, 179,
        183, 187, 191, 195, 199, 203, 207, 211, 215, 219, 223, 227, 231, 235, 239,
        243, 247, 251, 127, 129, 131, 133, 135, 137, 139, 141, 143, 145, 147, 149,
        151, 153, 155, 157, 159, 161, 163, 165, 167, 169, 171, 173, 175, 177, 179,
        181, 183, 185, 187, 189, 191, 193, 195, 197, 199, 201, 203, 205, 207, 209,
        211, 213, 215, 217, 219, 221, 223, 225, 227, 229, 231, 233, 235, 237, 239,
        241, 243, 245, 247, 249, 251, 253, 127,
    ];

    // VP8 arithmetic (boolean) encoder — faithful port of libwebp's VP8BitWriter. Produces
    // the exact bitstream the RFC 6386 boolean decoder (BoolDecoder above) consumes.
    private struct BoolEncoder
    {
        private byte[] buf;
        private int pos;
        private int range;
        private long value;
        private int run;
        private int nbBits;

        public void Init(int capacity)
        {
            buf = new byte[Math.Max(capacity, 256)];
            pos = 0;
            range = 254;
            value = 0;
            run = 0;
            nbBits = -8;
        }

        public void PutBit(int bit, int prob)
        {
            int split = (range * prob) >> 8;
            if (bit != 0)
            {
                value += split + 1;
                range -= split + 1;
            }
            else
            {
                range = split;
            }

            if (range < 127)
            {
                int shift = KNorm[range];
                range = KNewRange[range];
                value <<= shift;
                nbBits += shift;
                if (nbBits > 0)
                {
                    Flush();
                }
            }
        }

        public void PutBitUniform(int bit)
        {
            int split = range >> 1;
            if (bit != 0)
            {
                value += split + 1;
                range -= split + 1;
            }
            else
            {
                range = split;
            }

            if (range < 127)
            {
                range = KNewRange[range];
                value <<= 1;
                nbBits += 1;
                if (nbBits > 0)
                {
                    Flush();
                }
            }
        }

        public void PutBits(int v, int nb)
        {
            for (int mask = 1 << (nb - 1); mask != 0; mask >>= 1)
            {
                PutBitUniform((v & mask) != 0 ? 1 : 0);
            }
        }

        public byte[] Finish()
        {
            PutBits(0, 9 - nbBits);
            nbBits = 0;
            Flush();
            var result = new byte[pos];
            Buffer.BlockCopy(buf, 0, result, 0, pos);
            return result;
        }

        private void Flush()
        {
            int s = 8 + nbBits;
            long bits = value >> s;
            value -= bits << s;
            nbBits -= 8;
            if ((bits & 0xff) != 0xff)
            {
                Ensure(run + 1);
                if ((bits & 0x100) != 0 && pos > 0)
                {
                    buf[pos - 1]++;
                }

                if (run > 0)
                {
                    int fill = (bits & 0x100) != 0 ? 0x00 : 0xff;
                    for (; run > 0; run--)
                    {
                        buf[pos++] = (byte)fill;
                    }
                }

                buf[pos++] = (byte)(bits & 0xff);
            }
            else
            {
                run++;
            }
        }

        private void Ensure(int extra)
        {
            if (pos + extra > buf.Length)
            {
                Array.Resize(ref buf, Math.Max(buf.Length * 2, pos + extra));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8 Lossy Decoder
    // ═══════════════════════════════════════════════════════════════════

    public static ImageFrame Decode(byte[] data, byte[]? alphData)
    {
        if (data.Length < 10)
        {
            throw new InvalidDataException("VP8 data too small.");
        }

        // Parse frame tag (3 bytes)
        uint frameTag = (uint)data[0] | ((uint)data[1] << 8) | ((uint)data[2] << 16);
        int frameType = (int)(frameTag & 1); // 0=keyframe
        int version = (int)((frameTag >> 1) & 7);
        int showFrame = (int)((frameTag >> 4) & 1);
        int firstPartSize = (int)(frameTag >> 5);

        if (frameType != 0)
        {
            throw new InvalidDataException("Only VP8 keyframes are supported.");
        }

        // Keyframe header: start code + dimensions
        if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
        {
            throw new InvalidDataException("Invalid VP8 keyframe start code.");
        }

        int width = (data[6] | (data[7] << 8)) & 0x3FFF;
        int height = (data[8] | (data[9] << 8)) & 0x3FFF;

        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("Invalid VP8 dimensions.");
        }

        int mbWidth = (width + 15) / 16;
        int mbHeight = (height + 15) / 16;

        // Initialize coefficient probabilities with defaults
        byte[] coeffProbs = new byte[DefaultCoeffProbs.Length];
        Buffer.BlockCopy(DefaultCoeffProbs, 0, coeffProbs, 0, coeffProbs.Length);

        // Parse first partition (boolean-decoded frame header)
        var bd = new BoolDecoder();
        bd.Init(data, 10);

        // Color space and clamping
        int colorSpace = bd.ReadBool(128);
        int clampType = bd.ReadBool(128);

        // Segmentation
        int segmentationEnabled = bd.ReadBool(128);
        int[] segmentQuantizer = new int[4];
        int[] segmentLoopFilter = new int[4];
        int updateMap = 0;
        int absOrDelta = 0;
        byte[] segmentProb = [ 255, 255, 255 ];
        if (segmentationEnabled != 0)
        {
            updateMap = bd.ReadBool(128);
            int updateData = bd.ReadBool(128);
            if (updateData != 0)
            {
                absOrDelta = bd.ReadBool(128);
                for (int i = 0;i < 4;i++)
                {
                    segmentQuantizer[i] = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(7) : 0;
                }

                for (int i = 0;i < 4;i++)
                {
                    segmentLoopFilter[i] = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(6) : 0;
                }
            }
            if (updateMap != 0)
            {
                for (int i = 0;i < 3;i++)
                {
                    if (bd.ReadBool(128) != 0)
                    {
                        segmentProb[i] = (byte)bd.ReadLiteral(8);
                    }
                }
            }
        }

        // Loop filter header (RFC 6386 §9.4). The ref/mode filter-level deltas persist
        // across frames; for a lone keyframe they default to 0 and are updated if present.
        int filterSimple = bd.ReadBool(128);
        int filterLevel = bd.ReadLiteral(6);
        int sharpnessLevel = bd.ReadLiteral(3);
        int[] refLfDelta = new int[4];
        int[] modeLfDelta = new int[4];
        int useLfDelta = bd.ReadBool(128);
        if (useLfDelta != 0)
        {
            if (bd.ReadBool(128) != 0)
            {
                for (int i = 0;i < 4;i++)
                {
                    if (bd.ReadBool(128) != 0)
                    {
                        refLfDelta[i] = bd.ReadSignedLiteral(6);
                    }
                }

                for (int i = 0;i < 4;i++)
                {
                    if (bd.ReadBool(128) != 0)
                    {
                        modeLfDelta[i] = bd.ReadSignedLiteral(6);
                    }
                }
            }
        }

        // Effective filter type: 0 = off, 1 = simple, 2 = normal (complex).
        int filterType = filterLevel == 0 ? 0 : filterSimple != 0 ? 1 : 2;

        // Token partitions
        int log2Parts = bd.ReadLiteral(2);
        int numParts = 1 << log2Parts;

        // Dequantization indices
        int yacQi = bd.ReadLiteral(7);
        int ydcDelta = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(4) : 0;
        int y2dcDelta = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(4) : 0;
        int y2acDelta = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(4) : 0;
        int uvdcDelta = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(4) : 0;
        int uvacDelta = bd.ReadBool(128) != 0 ? bd.ReadSignedLiteral(4) : 0;

        // Per-segment dequantization matrices. Each macroblock selects one of four
        // segments (VP8 §9.3); with segmentation off, every MB uses segment 0. The
        // segment's base quantizer index is either absolute or a delta on the frame's
        // y-AC index, then the per-plane deltas above are applied on top.
        int[] ydcSeg = new int[4];
        int[] yacSeg = new int[4];
        int[] y2dcSeg = new int[4];
        int[] y2acSeg = new int[4];
        int[] uvdcSeg = new int[4];
        int[] uvacSeg = new int[4];
        for (int s = 0; s < 4; s++)
        {
            int baseQ = yacQi;
            if (segmentationEnabled != 0)
            {
                baseQ = absOrDelta != 0 ? segmentQuantizer[s] : yacQi + segmentQuantizer[s];
            }

            ydcSeg[s] = DcQLookup[Clamp128(baseQ + ydcDelta)];
            yacSeg[s] = AcQLookup[Clamp128(baseQ)];
            y2dcSeg[s] = DcQLookup[Clamp128(baseQ + y2dcDelta)] * 2;
            int y2 = AcQLookup[Clamp128(baseQ + y2acDelta)] * 155 / 100;
            y2acSeg[s] = y2 < 8 ? 8 : y2;
            int uvd = DcQLookup[Clamp128(baseQ + uvdcDelta)];
            uvdcSeg[s] = uvd > 132 ? 132 : uvd;
            uvacSeg[s] = AcQLookup[Clamp128(baseQ + uvacDelta)];
        }

        // Precompute the in-loop deblocking filter strength for each (segment, i4x4)
        // combination (RFC 6386 §15.2). f_limit==0 means the block is not filtered.
        int[,] fsLimit = new int[4, 2];
        int[,] fsIlevel = new int[4, 2];
        int[,] fsHev = new int[4, 2];
        if (filterType > 0)
        {
            for (int s = 0; s < 4; s++)
            {
                int baseLevel = filterLevel;
                if (segmentationEnabled != 0)
                {
                    baseLevel = segmentLoopFilter[s] + (absOrDelta != 0 ? 0 : filterLevel);
                }

                for (int i4 = 0; i4 <= 1; i4++)
                {
                    int level = baseLevel;
                    if (useLfDelta != 0)
                    {
                        level += refLfDelta[0];        // keyframe: intra reference frame
                        if (i4 != 0)
                        {
                            level += modeLfDelta[0];   // 4x4 (B_PRED) mode
                        }
                    }

                    level = level < 0 ? 0 : level > 63 ? 63 : level;
                    if (level > 0)
                    {
                        int ilevel = level;
                        if (sharpnessLevel > 0)
                        {
                            ilevel >>= sharpnessLevel > 4 ? 2 : 1;
                            if (ilevel > 9 - sharpnessLevel)
                            {
                                ilevel = 9 - sharpnessLevel;
                            }
                        }

                        if (ilevel < 1)
                        {
                            ilevel = 1;
                        }

                        fsIlevel[s, i4] = ilevel;
                        fsLimit[s, i4] = 2 * level + ilevel;
                        fsHev[s, i4] = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    }
                    else
                    {
                        fsLimit[s, i4] = 0; // no filtering
                    }
                }
            }
        }

        // Refresh entropy probs (keyframe always refreshes)
        bd.ReadBool(128); // refresh_entropy_probs

        // Coefficient probability updates
        for (int i = 0;i < 4;i++)
        {
            for (int j = 0;j < 8;j++)
            {
                for (int k = 0;k < 3;k++)
                {
                    for (int t = 0;t < 11;t++)
                    {
                        int idx = i * 264 + j * 33 + k * 11 + t;
                        if (bd.ReadBool(CoeffUpdateProbs[idx]) != 0)
                        {
                            coeffProbs[idx] = (byte)bd.ReadLiteral(8);
                        }
                    }
                }
            }
        }

        // mb_no_skip_coeff
        int mbNoSkip = bd.ReadBool(128);
        int probSkipFalse = mbNoSkip != 0 ? bd.ReadLiteral(8) : 0;

        // Token partition offsets
        int tokenDataStart = 10 + firstPartSize;
        int[] partOffsets = new int[numParts];
        int[] partSizes = new int[numParts];
        int partDataStart = tokenDataStart + (numParts > 1 ? (numParts - 1) * 3 : 0);

        if (numParts > 1)
        {
            int off = tokenDataStart;
            for (int p = 0;p < numParts - 1;p++)
            {
                partSizes[p] = data[off] | (data[off + 1] << 8) | (data[off + 2] << 16);
                off += 3;
            }
        }

        partOffsets[0] = partDataStart;
        for (int p = 1;p < numParts;p++)
        {
            partOffsets[p] = partOffsets[p - 1] + partSizes[p - 1];
        }

        partSizes[numParts - 1] = data.Length - partOffsets[numParts - 1];

        // Initialize token partition decoders
        var tokenDecoders = new BoolDecoder[numParts];
        for (int p = 0;p < numParts;p++)
        {
            tokenDecoders[p] = new BoolDecoder();
            if (partOffsets[p] + 2 <= data.Length)
            {
                tokenDecoders[p].Init(data, partOffsets[p]);
            }
        }

        // Reconstruction planes (one byte per luma/chroma sample; full macroblock grid).
        int yStride = mbWidth * 16;
        int uvStride = mbWidth * 8;
        int yPlaneSize = yStride * mbHeight * 16;
        int uvPlaneSize = uvStride * mbHeight * 8;
        byte[] yPlane = ArrayPool<byte>.Shared.Rent(yPlaneSize);
        byte[] uPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);
        byte[] vPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);
        try
        {

        // ── Per-macroblock working buffers (with a 1-sample border for prediction) ──
        // Pixel (x,y) lives at index Off + y*Bps + x, leaving room for the top row
        // (y=-1), left column (x=-1..-4 for the block rotation) and 4 above-right samples.
        const int Off = Bps + 8;
        byte[] yb = new byte[Bps * 18];
        byte[] ub = new byte[Bps * 10];
        byte[] vb = new byte[Bps * 10];

        // Top-row sample store (bottom row of the macroblock row above), per column.
        byte[] topY = new byte[mbWidth * 16];
        byte[] topU = new byte[mbWidth * 8];
        byte[] topV = new byte[mbWidth * 8];

        // Above/left prediction-mode context (0 = B_DC) for B_PRED submode decoding.
        int[] aboveSub = new int[mbWidth * 4];
        int[] leftSub = new int[4];
        // Above/left non-zero-coefficient context (1 = block had a non-zero coeff).
        int[] aboveNzY = new int[mbWidth * 4];
        int[] leftNzY = new int[4];
        int[] aboveNzU = new int[mbWidth * 2];
        int[] leftNzU = new int[2];
        int[] aboveNzV = new int[mbWidth * 2];
        int[] leftNzV = new int[2];
        int[] aboveNzY2 = new int[mbWidth];
        int leftNzY2 = 0;

        // Per-macroblock deblocking-filter parameters, resolved during decode and applied
        // in a raster-order pass after the whole frame is reconstructed.
        int[] mbFLimit = new int[mbWidth * mbHeight];
        int[] mbFIlevel = new int[mbWidth * mbHeight];
        int[] mbFHev = new int[mbWidth * mbHeight];
        bool[] mbFInner = new bool[mbWidth * mbHeight];

        short[][] yCoeffs = new short[16][];
        for (int i = 0; i < 16; i++)
        {
            yCoeffs[i] = new short[16];
        }

        short[][] uCoeffs = new short[4][];
        short[][] vCoeffs = new short[4][];
        for (int i = 0; i < 4; i++)
        {
            uCoeffs[i] = new short[16];
            vCoeffs[i] = new short[16];
        }

        short[] y2 = new short[16];
        short[] dcVals = new short[16];
        int[] imodes = new int[16];
        short[] residual = new short[16];

        // Decode + reconstruct macroblocks.
        for (int mbRow = 0; mbRow < mbHeight; mbRow++)
        {
            ref var tokenBd = ref tokenDecoders[mbRow % numParts];

            // Reset per-row left contexts and left border (=129).
            for (int i = 0; i < 4; i++)
            {
                leftSub[i] = 0;
                leftNzY[i] = 0;
            }

            leftNzU[0] = leftNzU[1] = 0;
            leftNzV[0] = leftNzV[1] = 0;
            leftNzY2 = 0;
            for (int y = 0; y < 16; y++)
            {
                yb[Off + y * Bps - 1] = 129;
            }

            for (int y = 0; y < 8; y++)
            {
                ub[Off + y * Bps - 1] = 129;
                vb[Off + y * Bps - 1] = 129;
            }

            if (mbRow > 0)
            {
                yb[Off - Bps - 1] = 129;
                ub[Off - Bps - 1] = 129;
                vb[Off - Bps - 1] = 129;
            }

            for (int mbCol = 0; mbCol < mbWidth; mbCol++)
            {
                // ── Parse per-macroblock modes from the first (header) partition ──
                // Segment id (only in the bitstream when the segmentation map is updated),
                // then the coefficient-skip flag, then the intra prediction modes.
                int segment = 0;
                if (segmentationEnabled != 0 && updateMap != 0)
                {
                    segment = bd.ReadBool(segmentProb[0]) == 0
                        ? bd.ReadBool(segmentProb[1])
                        : bd.ReadBool(segmentProb[2]) + 2;
                }

                int ydc = ydcSeg[segment], yac = yacSeg[segment];
                int y2dc = y2dcSeg[segment], y2ac = y2acSeg[segment];
                int uvdc = uvdcSeg[segment], uvac = uvacSeg[segment];

                bool skipCoeff = mbNoSkip != 0 && bd.ReadBool(probSkipFalse) != 0;
                bool isI4x4 = bd.ReadBool(145) == 0;

                int ymode = 0;
                if (!isI4x4)
                {
                    // 16x16 luma mode tree: DC=0, TM=1, V=2, H=3.
                    ymode = bd.ReadBool(156) != 0
                        ? (bd.ReadBool(128) != 0 ? 1 : 3)
                        : (bd.ReadBool(163) != 0 ? 2 : 0);
                    for (int k = 0; k < 4; k++)
                    {
                        aboveSub[mbCol * 4 + k] = ymode;
                        leftSub[k] = ymode;
                    }
                }
                else
                {
                    // 16 per-subblock B_PRED submodes, context = (above, left).
                    for (int y = 0; y < 4; y++)
                    {
                        int lmode = leftSub[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int m = DecodeBMode(ref bd, aboveSub[mbCol * 4 + x], lmode);
                            imodes[y * 4 + x] = m;
                            aboveSub[mbCol * 4 + x] = m;
                            lmode = m;
                        }

                        leftSub[y] = lmode;
                    }
                }

                // 8x8 chroma mode tree: DC=0, TM=1, V=2, H=3.
                int uvmode = bd.ReadBool(142) == 0 ? 0
                    : bd.ReadBool(114) == 0 ? 2
                    : bd.ReadBool(183) != 0 ? 1 : 3;

                // ── Parse residuals from the token partition ──
                for (int i = 0; i < 16; i++)
                {
                    Array.Clear(yCoeffs[i], 0, 16);
                }

                for (int i = 0; i < 4; i++)
                {
                    Array.Clear(uCoeffs[i], 0, 16);
                    Array.Clear(vCoeffs[i], 0, 16);
                }

                int firstY = isI4x4 ? 0 : 1;
                int acBand = isI4x4 ? 3 : 0;
                bool anyNz = false; // any non-zero coefficient in this MB (drives inner-edge filtering)

                if (skipCoeff)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        aboveNzY[mbCol * 4 + k] = 0;
                        leftNzY[k] = 0;
                    }

                    aboveNzU[mbCol * 2] = aboveNzU[mbCol * 2 + 1] = 0;
                    aboveNzV[mbCol * 2] = aboveNzV[mbCol * 2 + 1] = 0;
                    leftNzU[0] = leftNzU[1] = 0;
                    leftNzV[0] = leftNzV[1] = 0;
                    if (!isI4x4)
                    {
                        aboveNzY2[mbCol] = 0;
                        leftNzY2 = 0;
                    }
                }
                else
                {
                    if (!isI4x4)
                    {
                        // Y2 (WHT) block carries the 16 luma DC coefficients.
                        Array.Clear(y2, 0, 16);
                        int ctx = aboveNzY2[mbCol] + leftNzY2;
                        int nz = GetCoeffs(ref tokenBd, coeffProbs, 1, ctx, y2dc, y2ac, 0, y2);
                        aboveNzY2[mbCol] = leftNzY2 = nz > 0 ? 1 : 0;
                        if (nz > 1)
                        {
                            InverseWht(y2, dcVals);
                        }
                        else
                        {
                            short dc0 = (short)((y2[0] + 3) >> 3);
                            for (int i = 0; i < 16; i++)
                            {
                                dcVals[i] = dc0;
                            }
                        }

                        for (int i = 0; i < 16; i++)
                        {
                            if (dcVals[i] != 0)
                            {
                                anyNz = true;
                                break;
                            }
                        }
                    }

                    for (int y = 0; y < 4; y++)
                    {
                        int l = leftNzY[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int n = y * 4 + x;
                            int ctx = l + aboveNzY[mbCol * 4 + x];
                            int nz = GetCoeffs(ref tokenBd, coeffProbs, acBand, ctx, ydc, yac, firstY, yCoeffs[n]);
                            int f = nz > firstY ? 1 : 0;
                            l = f;
                            aboveNzY[mbCol * 4 + x] = f;
                            anyNz |= f != 0;
                            if (!isI4x4)
                            {
                                yCoeffs[n][0] = dcVals[n];
                            }
                        }

                        leftNzY[y] = l;
                    }

                    for (int y = 0; y < 2; y++)
                    {
                        int l = leftNzU[y];
                        for (int x = 0; x < 2; x++)
                        {
                            int ctx = l + aboveNzU[mbCol * 2 + x];
                            int nz = GetCoeffs(ref tokenBd, coeffProbs, 2, ctx, uvdc, uvac, 0, uCoeffs[y * 2 + x]);
                            int f = nz > 0 ? 1 : 0;
                            l = f;
                            aboveNzU[mbCol * 2 + x] = f;
                            anyNz |= f != 0;
                        }

                        leftNzU[y] = l;
                    }

                    for (int y = 0; y < 2; y++)
                    {
                        int l = leftNzV[y];
                        for (int x = 0; x < 2; x++)
                        {
                            int ctx = l + aboveNzV[mbCol * 2 + x];
                            int nz = GetCoeffs(ref tokenBd, coeffProbs, 2, ctx, uvdc, uvac, 0, vCoeffs[y * 2 + x]);
                            int f = nz > 0 ? 1 : 0;
                            l = f;
                            aboveNzV[mbCol * 2 + x] = f;
                            anyNz |= f != 0;
                        }

                        leftNzV[y] = l;
                    }
                }

                // Record this MB's deblocking-filter parameters for the post-pass.
                int fi4 = isI4x4 ? 1 : 0;
                int mbIdx = mbRow * mbWidth + mbCol;
                mbFLimit[mbIdx] = fsLimit[segment, fi4];
                mbFIlevel[mbIdx] = fsIlevel[segment, fi4];
                mbFHev[mbIdx] = fsHev[segment, fi4];
                mbFInner[mbIdx] = isI4x4 || anyNz;

                // ── Reconstruct: rotate in the left column, refresh the top row ──
                if (mbCol > 0)
                {
                    for (int y = -1; y < 16; y++)
                    {
                        for (int k = 1; k <= 4; k++)
                        {
                            yb[Off + y * Bps - k] = yb[Off + y * Bps + 16 - k];
                        }
                    }

                    for (int y = -1; y < 8; y++)
                    {
                        for (int k = 1; k <= 4; k++)
                        {
                            ub[Off + y * Bps - k] = ub[Off + y * Bps + 8 - k];
                            vb[Off + y * Bps - k] = vb[Off + y * Bps + 8 - k];
                        }
                    }
                }

                if (mbRow > 0)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        yb[Off - Bps + x] = topY[mbCol * 16 + x];
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        ub[Off - Bps + x] = topU[mbCol * 8 + x];
                        vb[Off - Bps + x] = topV[mbCol * 8 + x];
                    }
                }
                else
                {
                    for (int x = -1; x < 20; x++)
                    {
                        yb[Off - Bps + x] = 127;
                    }

                    for (int x = -1; x < 9; x++)
                    {
                        ub[Off - Bps + x] = 127;
                        vb[Off - Bps + x] = 127;
                    }
                }

                // ── Luma prediction + residual ──
                if (isI4x4)
                {
                    // Above-right samples for the top subblock row (from the MB above-right,
                    // replicated at the right border), then replicated down to feed the
                    // rightmost subblock column (VP8 §12.3 above-right rule).
                    if (mbRow > 0)
                    {
                        if (mbCol >= mbWidth - 1)
                        {
                            byte tr = topY[mbCol * 16 + 15];
                            for (int k = 0; k < 4; k++)
                            {
                                yb[Off - Bps + 16 + k] = tr;
                            }
                        }
                        else
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                yb[Off - Bps + 16 + k] = topY[(mbCol + 1) * 16 + k];
                            }
                        }
                    }

                    for (int k = 0; k < 4; k++)
                    {
                        byte tr = yb[Off - Bps + 16 + k];
                        yb[Off + 3 * Bps + 16 + k] = tr;
                        yb[Off + 7 * Bps + 16 + k] = tr;
                        yb[Off + 11 * Bps + 16 + k] = tr;
                    }

                    for (int n = 0; n < 16; n++)
                    {
                        int dst = Off + (n / 4) * 4 * Bps + (n % 4) * 4;
                        Pred4(yb, dst, imodes[n]);
                        AddResidual(yb, dst, yCoeffs[n], residual);
                    }
                }
                else
                {
                    PredBlock(yb, Off, 16, CheckMode(mbCol, mbRow, ymode));
                    for (int n = 0; n < 16; n++)
                    {
                        int dst = Off + (n / 4) * 4 * Bps + (n % 4) * 4;
                        AddResidual(yb, dst, yCoeffs[n], residual);
                    }
                }

                // ── Chroma prediction + residual ──
                int uvPf = CheckMode(mbCol, mbRow, uvmode);
                PredBlock(ub, Off, 8, uvPf);
                PredBlock(vb, Off, 8, uvPf);
                for (int n = 0; n < 4; n++)
                {
                    int dst = Off + (n / 2) * 4 * Bps + (n % 2) * 4;
                    AddResidual(ub, dst, uCoeffs[n], residual);
                    AddResidual(vb, dst, vCoeffs[n], residual);
                }

                // ── Emit reconstructed samples + stash the bottom row for the next MB row ──
                int yBase = mbRow * 16 * yStride + mbCol * 16;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        yPlane[yBase + y * yStride + x] = yb[Off + y * Bps + x];
                    }
                }

                int uvBase = mbRow * 8 * uvStride + mbCol * 8;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        uPlane[uvBase + y * uvStride + x] = ub[Off + y * Bps + x];
                        vPlane[uvBase + y * uvStride + x] = vb[Off + y * Bps + x];
                    }
                }

                for (int x = 0; x < 16; x++)
                {
                    topY[mbCol * 16 + x] = yb[Off + 15 * Bps + x];
                }

                for (int x = 0; x < 8; x++)
                {
                    topU[mbCol * 8 + x] = ub[Off + 7 * Bps + x];
                    topV[mbCol * 8 + x] = vb[Off + 7 * Bps + x];
                }

            }
        }

        // ── In-loop deblocking filter (RFC 6386 §15), applied in raster order ──
        if (filterType > 0)
        {
            for (int mbRow = 0; mbRow < mbHeight; mbRow++)
            {
                for (int mbCol = 0; mbCol < mbWidth; mbCol++)
                {
                    int mbIdx = mbRow * mbWidth + mbCol;
                    int limit = mbFLimit[mbIdx];
                    if (limit == 0)
                    {
                        continue;
                    }

                    int ilevel = mbFIlevel[mbIdx];
                    int hev = mbFHev[mbIdx];
                    bool inner = mbFInner[mbIdx];
                    int yOff = mbRow * 16 * yStride + mbCol * 16;
                    int uvOff = mbRow * 8 * uvStride + mbCol * 8;

                    if (filterType == 1)
                    {
                        // Simple filter: luma only.
                        if (mbCol > 0)
                        {
                            SimpleHFilter16(yPlane, yOff, yStride, limit + 4);
                        }

                        if (inner)
                        {
                            SimpleHFilter16Inner(yPlane, yOff, yStride, limit);
                        }

                        if (mbRow > 0)
                        {
                            SimpleVFilter16(yPlane, yOff, yStride, limit + 4);
                        }

                        if (inner)
                        {
                            SimpleVFilter16Inner(yPlane, yOff, yStride, limit);
                        }
                    }
                    else
                    {
                        if (mbCol > 0)
                        {
                            FilterLoop(yPlane, yOff, 1, yStride, 16, limit + 4, ilevel, hev, false);
                            FilterLoop(uPlane, uvOff, 1, uvStride, 8, limit + 4, ilevel, hev, false);
                            FilterLoop(vPlane, uvOff, 1, uvStride, 8, limit + 4, ilevel, hev, false);
                        }

                        if (inner)
                        {
                            for (int k = 1; k <= 3; k++)
                            {
                                FilterLoop(yPlane, yOff + k * 4, 1, yStride, 16, limit, ilevel, hev, true);
                            }

                            FilterLoop(uPlane, uvOff + 4, 1, uvStride, 8, limit, ilevel, hev, true);
                            FilterLoop(vPlane, uvOff + 4, 1, uvStride, 8, limit, ilevel, hev, true);
                        }

                        if (mbRow > 0)
                        {
                            FilterLoop(yPlane, yOff, yStride, 1, 16, limit + 4, ilevel, hev, false);
                            FilterLoop(uPlane, uvOff, uvStride, 1, 8, limit + 4, ilevel, hev, false);
                            FilterLoop(vPlane, uvOff, uvStride, 1, 8, limit + 4, ilevel, hev, false);
                        }

                        if (inner)
                        {
                            for (int k = 1; k <= 3; k++)
                            {
                                FilterLoop(yPlane, yOff + k * 4 * yStride, yStride, 1, 16, limit, ilevel, hev, true);
                            }

                            FilterLoop(uPlane, uvOff + 4 * uvStride, uvStride, 1, 8, limit, ilevel, hev, true);
                            FilterLoop(vPlane, uvOff + 4 * uvStride, uvStride, 1, 8, limit, ilevel, hev, true);
                        }
                    }
                }
            }
        }

        // Upsample the 4:2:0 chroma to full resolution using libwebp's "fancy" (bilinear
        // 9:3:3:1) upsampler so colour edges match the reference decoder bit-for-bit.
        byte[] uFull = ArrayPool<byte>.Shared.Rent(width * height);
        byte[] vFull = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            int uvHeight = (height + 1) / 2;
            UpsampleUvPair(uPlane, vPlane, uvStride, 0, 0, uFull, vFull, 0, 0, width, false);
            for (int k = 0; 2 * k + 1 < height; k++)
            {
                int topOut = 2 * k + 1;
                int botOut = 2 * k + 2;
                int cyCur = Math.Min(k + 1, uvHeight - 1);
                UpsampleUvPair(uPlane, vPlane, uvStride, k, cyCur, uFull, vFull, topOut, botOut, width, botOut < height);
            }

            // Convert YUV to ImageFrame using libwebp's exact fixed-point constants (dsp/yuv.h).
            var frame = new ImageFrame();
            bool hasAlpha = alphData != null;
            frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
            frame.Compression = CompressionType.WebP;
            int channels = frame.NumberOfChannels;

            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                int yBase = y * yStride;
                int uvBase = y * width;
                for (int x = 0; x < width; x++)
                {
                    int yScaled = yPlane[yBase + x] * 19077 >> 8;
                    int uVal = uFull[uvBase + x];
                    int vVal = vFull[uvBase + x];
                    byte r = YuvClip8(yScaled + (vVal * 26149 >> 8) - 14234);
                    byte g = YuvClip8(yScaled - (uVal * 6419 >> 8) - (vVal * 13320 >> 8) + 8708);
                    byte b = YuvClip8(yScaled + (uVal * 33050 >> 8) - 17685);

                    int off = x * channels;
                    row[off] = Quantum.ScaleFromByte(r);
                    row[off + 1] = Quantum.ScaleFromByte(g);
                    row[off + 2] = Quantum.ScaleFromByte(b);
                    if (hasAlpha)
                    {
                        row[off + 3] = Quantum.Opaque;
                    }
                }
            }

            return frame;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(uFull);
            ArrayPool<byte>.Shared.Return(vFull);
        }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(yPlane);
            ArrayPool<byte>.Shared.Return(uPlane);
            ArrayPool<byte>.Shared.Return(vPlane);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8 Lossy Encoder (simplified: DC prediction, keyframe only)
    // ═══════════════════════════════════════════════════════════════════

    public static byte[] Encode(ImageFrame image, int quality)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int mbWidth = (width + 15) / 16;
        int mbHeight = (height + 15) / 16;
        int channels = image.NumberOfChannels;

        // Convert to YUV420
        int yStride = mbWidth * 16;
        int uvStride = mbWidth * 8;
        int yPlaneSize = mbHeight * 16 * yStride;
        int uvPlaneSize = mbHeight * 8 * uvStride;
        byte[] yPlane = ArrayPool<byte>.Shared.Rent(yPlaneSize);
        byte[] uPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);
        byte[] vPlane = ArrayPool<byte>.Shared.Rent(uvPlaneSize);
        Array.Clear(yPlane, 0, yPlaneSize);
        Array.Clear(uPlane, 0, uvPlaneSize);
        Array.Clear(vPlane, 0, uvPlaneSize);
        try
        {

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                int r = Quantum.ScaleToByte(row[off]);
                int g = Quantum.ScaleToByte(row[off + 1]);
                int b = Quantum.ScaleToByte(row[off + 2]);
                yPlane[y * yStride + x] = ClampByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    uPlane[(y / 2) * uvStride + (x / 2)] = ClampByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    vPlane[(y / 2) * uvStride + (x / 2)] = ClampByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }

        // Map quality (0..100) to a base quantizer index (0 = finest .. 127 = coarsest).
        int qi = Math.Clamp((int)Math.Round((100 - quality) * 127.0 / 100.0), 0, 127);
        int ydc = DcQLookup[qi];
        int yac = AcQLookup[qi];
        int y2dc = DcQLookup[qi] * 2;
        int y2ac = Math.Max(AcQLookup[qi] * 155 / 100, 8);
        int uvdc = Math.Min(DcQLookup[qi], 132);
        int uvac = AcQLookup[qi];

        // ── First (header) partition ──
        // Encodes 16x16 DC-predicted intra keyframe, no segmentation, loop filter off
        // (level 0 → the decoder skips filtering), single token partition, default coeff
        // probabilities. Every macroblock is coded (never skipped) so the token stream is
        // simple; the reconstruction below keeps prediction in lockstep with the decoder.
        const int probSkipFalse = 255;
        var headerEnc = new BoolEncoder();
        headerEnc.Init(65536);
        headerEnc.PutBit(0, 128); // colour space
        headerEnc.PutBit(0, 128); // clamping type
        headerEnc.PutBit(0, 128); // segmentation disabled
        headerEnc.PutBit(0, 128); // filter type (simple)
        headerEnc.PutBits(0, 6);  // filter level 0
        headerEnc.PutBits(0, 3);  // sharpness
        headerEnc.PutBit(0, 128); // no loop-filter deltas
        headerEnc.PutBits(0, 2);  // log2(token partitions) = 0
        headerEnc.PutBits(qi, 7); // base quantizer index
        headerEnc.PutBit(0, 128); // no y_dc delta
        headerEnc.PutBit(0, 128); // no y2_dc delta
        headerEnc.PutBit(0, 128); // no y2_ac delta
        headerEnc.PutBit(0, 128); // no uv_dc delta
        headerEnc.PutBit(0, 128); // no uv_ac delta
        headerEnc.PutBit(0, 128); // refresh_entropy_probs
        for (int idx = 0; idx < 4 * 264; idx++)
        {
            headerEnc.PutBit(0, CoeffUpdateProbs[idx]); // no coeff-probability updates
        }

        headerEnc.PutBit(1, 128);                 // mb_no_skip_coeff enabled
        headerEnc.PutBits(probSkipFalse, 8);

        var tokenEnc = new BoolEncoder();
        tokenEnc.Init(Math.Max(width * height * 2, 4096));

        // Working buffers (with the 1-sample prediction border) + non-zero contexts, exactly
        // as the decoder uses them — so the reconstructed samples the encoder predicts from
        // are identical to what the decoder will produce.
        const int Off = Bps + 8;
        byte[] yb = new byte[Bps * 18];
        byte[] ub = new byte[Bps * 10];
        byte[] vb = new byte[Bps * 10];
        byte[] topY = new byte[mbWidth * 16];
        byte[] topU = new byte[mbWidth * 8];
        byte[] topV = new byte[mbWidth * 8];
        int[] aboveNzY = new int[mbWidth * 4];
        int[] leftNzY = new int[4];
        int[] aboveNzU = new int[mbWidth * 2];
        int[] leftNzU = new int[2];
        int[] aboveNzV = new int[mbWidth * 2];
        int[] leftNzV = new int[2];
        int[] aboveNzY2 = new int[mbWidth];
        int leftNzY2 = 0;

        short[] dct = new short[16];
        short[] deq = new short[16];
        short[] residual = new short[16];
        short[] rawDc = new short[16];
        short[] y2lvl = new short[16];
        short[] dcDeq = new short[16];
        short[][] yLvl = new short[16][];
        for (int i = 0; i < 16; i++)
        {
            yLvl[i] = new short[16];
        }

        short[][] uLvl = new short[4][];
        short[][] vLvl = new short[4][];
        for (int i = 0; i < 4; i++)
        {
            uLvl[i] = new short[16];
            vLvl[i] = new short[16];
        }

        for (int mbRow = 0; mbRow < mbHeight; mbRow++)
        {
            for (int i = 0; i < 4; i++)
            {
                leftNzY[i] = 0;
            }

            leftNzU[0] = leftNzU[1] = 0;
            leftNzV[0] = leftNzV[1] = 0;
            leftNzY2 = 0;
            for (int y = 0; y < 16; y++)
            {
                yb[Off + y * Bps - 1] = 129;
            }

            for (int y = 0; y < 8; y++)
            {
                ub[Off + y * Bps - 1] = 129;
                vb[Off + y * Bps - 1] = 129;
            }

            if (mbRow > 0)
            {
                yb[Off - Bps - 1] = 129;
                ub[Off - Bps - 1] = 129;
                vb[Off - Bps - 1] = 129;
            }

            for (int mbCol = 0; mbCol < mbWidth; mbCol++)
            {
                // Bring in reconstructed left column + top row (mirror of the decoder).
                if (mbCol > 0)
                {
                    for (int y = -1; y < 16; y++)
                    {
                        for (int k = 1; k <= 4; k++)
                        {
                            yb[Off + y * Bps - k] = yb[Off + y * Bps + 16 - k];
                        }
                    }

                    for (int y = -1; y < 8; y++)
                    {
                        for (int k = 1; k <= 4; k++)
                        {
                            ub[Off + y * Bps - k] = ub[Off + y * Bps + 8 - k];
                            vb[Off + y * Bps - k] = vb[Off + y * Bps + 8 - k];
                        }
                    }
                }

                if (mbRow > 0)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        yb[Off - Bps + x] = topY[mbCol * 16 + x];
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        ub[Off - Bps + x] = topU[mbCol * 8 + x];
                        vb[Off - Bps + x] = topV[mbCol * 8 + x];
                    }
                }
                else
                {
                    for (int x = -1; x < 20; x++)
                    {
                        yb[Off - Bps + x] = 127;
                    }

                    for (int x = -1; x < 9; x++)
                    {
                        ub[Off - Bps + x] = 127;
                        vb[Off - Bps + x] = 127;
                    }
                }

                // Choose the best 16x16 luma and 8x8 chroma prediction modes (lowest SAD),
                // leaving the working buffers predicted with the winners.
                int ymode = SelectLumaMode(yb, yPlane, yStride, mbCol, mbRow);
                int uvmode = SelectChromaMode(ub, vb, uPlane, vPlane, uvStride, mbCol, mbRow);

                // Header: not skipped, 16x16 intra, chosen luma + chroma modes.
                headerEnc.PutBit(0, probSkipFalse);
                headerEnc.PutBit(1, 145); // is_i4x4 = 0
                WriteLumaMode16(ref headerEnc, ymode);
                WriteChromaMode(ref headerEnc, uvmode);

                // ── Luma: forward-transform the residual, quantize ──
                for (int n = 0; n < 16; n++)
                {
                    int dstIdx = Off + (n / 4) * 4 * Bps + (n % 4) * 4;
                    int ox = mbCol * 16 + (n % 4) * 4;
                    int oy = mbRow * 16 + (n / 4) * 4;
                    ForwardDct(yPlane, yStride, ox, oy, yb, dstIdx, dct);
                    rawDc[n] = dct[0];
                    Array.Clear(yLvl[n], 0, 16);
                    for (int j = 1; j < 16; j++)
                    {
                        yLvl[n][j] = QuantizeLevel(dct[j], yac);
                    }
                }

                // Y2 (WHT of the 16 luma DCs), quantize + encode; dequantize + inverse WHT
                // to recover the per-subblock DC used for reconstruction.
                ForwardWht(rawDc, dct);
                Array.Clear(y2lvl, 0, 16);
                y2lvl[0] = QuantizeLevel(dct[0], y2dc);
                for (int j = 1; j < 16; j++)
                {
                    y2lvl[j] = QuantizeLevel(dct[j], y2ac);
                }

                int ctxY2 = aboveNzY2[mbCol] + leftNzY2;
                bool y2nz = EncodeBlock(ref tokenEnc, DefaultCoeffProbs, 1, ctxY2, 0, y2lvl);
                aboveNzY2[mbCol] = leftNzY2 = y2nz ? 1 : 0;

                deq[0] = (short)(y2lvl[0] * y2dc);
                for (int j = 1; j < 16; j++)
                {
                    deq[j] = (short)(y2lvl[j] * y2ac);
                }

                InverseWht(deq, dcDeq);

                // Encode luma AC (skipping DC) with neighbour context, then reconstruct.
                for (int y = 0; y < 4; y++)
                {
                    int l = leftNzY[y];
                    for (int x = 0; x < 4; x++)
                    {
                        int n = y * 4 + x;
                        int ctx = l + aboveNzY[mbCol * 4 + x];
                        bool nz = EncodeBlock(ref tokenEnc, DefaultCoeffProbs, 0, ctx, 1, yLvl[n]);
                        int f = nz ? 1 : 0;
                        l = f;
                        aboveNzY[mbCol * 4 + x] = f;

                        deq[0] = dcDeq[n];
                        for (int j = 1; j < 16; j++)
                        {
                            deq[j] = (short)(yLvl[n][j] * yac);
                        }

                        AddResidual(yb, Off + y * 4 * Bps + x * 4, deq, residual);
                    }

                    leftNzY[y] = l;
                }

                // ── Chroma: transform, quantize, encode, reconstruct (already predicted) ──
                EncodeChromaPlane(ub, uPlane, uvStride, mbCol, mbRow, uvdc, uvac, uLvl, aboveNzU, leftNzU, ref tokenEnc, deq, residual);
                EncodeChromaPlane(vb, vPlane, uvStride, mbCol, mbRow, uvdc, uvac, vLvl, aboveNzV, leftNzV, ref tokenEnc, deq, residual);

                // Stash the reconstructed bottom row for the next macroblock row.
                for (int x = 0; x < 16; x++)
                {
                    topY[mbCol * 16 + x] = yb[Off + 15 * Bps + x];
                }

                for (int x = 0; x < 8; x++)
                {
                    topU[mbCol * 8 + x] = ub[Off + 7 * Bps + x];
                    topV[mbCol * 8 + x] = vb[Off + 7 * Bps + x];
                }
            }
        }

        byte[] headerData = headerEnc.Finish();
        byte[] tokenData = tokenEnc.Finish();

        // Build VP8 frame
        int part0Size = headerData.Length;
        int totalSize = 10 + part0Size + tokenData.Length;
        byte[] frame = new byte[totalSize];

        // Frame tag (keyframe, version 0, show=1)
        uint tag = 0 | (0u << 1) | (1u << 4) | ((uint)part0Size << 5);
        frame[0] = (byte)(tag & 0xFF);
        frame[1] = (byte)((tag >> 8) & 0xFF);
        frame[2] = (byte)((tag >> 16) & 0xFF);

        // Keyframe header
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        frame[6] = (byte)(width & 0xFF);
        frame[7] = (byte)((width >> 8) & 0xFF);
        frame[8] = (byte)(height & 0xFF);
        frame[9] = (byte)((height >> 8) & 0xFF);

        Buffer.BlockCopy(headerData, 0, frame, 10, headerData.Length);
        Buffer.BlockCopy(tokenData, 0, frame, 10 + headerData.Length, tokenData.Length);

        return frame;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(yPlane);
            ArrayPool<byte>.Shared.Return(uPlane);
            ArrayPool<byte>.Shared.Return(vPlane);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Coefficient Decoding / Encoding
    // ═══════════════════════════════════════════════════════════════════

    // Extra-bit probability tables for the DCT coefficient "categories" (RFC 6386 §13.2).
    private static readonly byte[] Cat3 = [ 173, 148, 140 ];
    private static readonly byte[] Cat4 = [ 176, 155, 140, 135 ];
    private static readonly byte[] Cat5 = [ 180, 157, 141, 134, 130 ];
    private static readonly byte[] Cat6 = [ 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 ];
    private static readonly byte[][] Cat3456 = [ Cat3, Cat4, Cat5, Cat6 ];

    // Decodes one 4x4 block of DCT coefficients from a token partition and dequantizes
    // them in place. Faithful port of libwebp's GetCoeffsFast (dec/vp8_dec.c).
    //   type     — coefficient plane band set: 0=luma-after-Y2, 1=Y2, 2=chroma, 3=i4x4 luma.
    //   ctx      — initial context (0..2) from the number of non-zero neighbouring blocks.
    //   firstN   — starting coefficient index (1 when the DC comes from the Y2/WHT block).
    // Returns the index one past the last non-zero coefficient (0 = immediate EOB). VP8
    // forbids an EOB token immediately after a zero coefficient, so the inner zero-run
    // loop advances without re-reading the EOB bit — matching the reference decoder.
    private static int GetCoeffs(ref BoolDecoder bd, byte[] probs, int type, int ctx, int dqDc, int dqAc, int firstN, short[] outCoeffs)
    {
        int n = firstN;
        int ctxCur = ctx;
        int off = type * 264 + Bands[n] * 33 + ctxCur * 11;
        for (; n < 16; n++)
        {
            if (bd.ReadBool(probs[off + 0]) == 0)
            {
                return n; // EOB
            }

            while (bd.ReadBool(probs[off + 1]) == 0)
            {
                n++;
                if (n == 16)
                {
                    return 16;
                }

                off = type * 264 + Bands[n] * 33; // context 0 during a zero-run
            }

            int v;
            if (bd.ReadBool(probs[off + 2]) == 0)
            {
                v = 1;
                ctxCur = 1;
            }
            else
            {
                v = GetLargeValue(ref bd, probs, off);
                ctxCur = 2;
            }

            int val = bd.ReadBool(128) != 0 ? -v : v;
            outCoeffs[ZigZag[n]] = (short)(val * (n > 0 ? dqAc : dqDc));

            int nn = n + 1;
            off = type * 264 + (nn < 16 ? Bands[nn] : 0) * 33 + ctxCur * 11;
        }

        return 16;
    }

    // Magnitude of a coefficient greater than 1 (RFC 6386 §13.2). Ported verbatim from
    // libwebp's GetLargeValue; `p` is the base index of the current position's probs.
    private static int GetLargeValue(ref BoolDecoder bd, byte[] probs, int p)
    {
        int v;
        if (bd.ReadBool(probs[p + 3]) == 0)
        {
            v = bd.ReadBool(probs[p + 4]) == 0 ? 2 : 3 + bd.ReadBool(probs[p + 5]);
        }
        else if (bd.ReadBool(probs[p + 6]) == 0)
        {
            if (bd.ReadBool(probs[p + 7]) == 0)
            {
                v = 5 + bd.ReadBool(159);
            }
            else
            {
                v = 7 + 2 * bd.ReadBool(165);
                v += bd.ReadBool(145);
            }
        }
        else
        {
            int bit1 = bd.ReadBool(probs[p + 8]);
            int bit0 = bd.ReadBool(probs[p + 9 + bit1]);
            int cat = 2 * bit1 + bit0;
            byte[] tab = Cat3456[cat];
            v = 0;
            for (int t = 0; t < tab.Length; t++)
            {
                v += v + bd.ReadBool(tab[t]);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }

    private static readonly int[] Cat6Probs = [ 254, 254, 254, 252, 249, 243, 230, 196, 177, 153, 140 ];

    // Picks the 16x16 luma prediction mode (0=DC,1=TM,2=V,3=H) with the lowest sum of
    // absolute residuals and leaves the working buffer predicted with it.
    private static int SelectLumaMode(byte[] yb, byte[] plane, int stride, int mbCol, int mbRow)
    {
        const int Off = Bps + 8;
        int ox = mbCol * 16, oy = mbRow * 16;
        int best = 0;
        long bestSad = long.MaxValue;
        for (int mode = 0; mode < 4; mode++)
        {
            PredBlock(yb, Off, 16, mode == 0 ? CheckMode(mbCol, mbRow, 0) : mode);
            long sad = 0;
            for (int r = 0; r < 16; r++)
            {
                int pb = Off + r * Bps;
                int ob = (oy + r) * stride + ox;
                for (int c = 0; c < 16; c++)
                {
                    int d = plane[ob + c] - yb[pb + c];
                    sad += d < 0 ? -d : d;
                }
            }

            if (sad < bestSad)
            {
                bestSad = sad;
                best = mode;
            }
        }

        PredBlock(yb, Off, 16, best == 0 ? CheckMode(mbCol, mbRow, 0) : best);
        return best;
    }

    // Picks one 8x8 chroma prediction mode shared by U and V (lowest combined SAD) and
    // leaves both chroma buffers predicted with it.
    private static int SelectChromaMode(byte[] ub, byte[] vb, byte[] up, byte[] vp, int stride, int mbCol, int mbRow)
    {
        const int Off = Bps + 8;
        int ox = mbCol * 8, oy = mbRow * 8;
        int best = 0;
        long bestSad = long.MaxValue;
        for (int mode = 0; mode < 4; mode++)
        {
            int f = mode == 0 ? CheckMode(mbCol, mbRow, 0) : mode;
            PredBlock(ub, Off, 8, f);
            PredBlock(vb, Off, 8, f);
            long sad = 0;
            for (int r = 0; r < 8; r++)
            {
                int pb = Off + r * Bps;
                int ob = (oy + r) * stride + ox;
                for (int c = 0; c < 8; c++)
                {
                    int du = up[ob + c] - ub[pb + c];
                    int dv = vp[ob + c] - vb[pb + c];
                    sad += (du < 0 ? -du : du) + (dv < 0 ? -dv : dv);
                }
            }

            if (sad < bestSad)
            {
                bestSad = sad;
                best = mode;
            }
        }

        int bf = best == 0 ? CheckMode(mbCol, mbRow, 0) : best;
        PredBlock(ub, Off, 8, bf);
        PredBlock(vb, Off, 8, bf);
        return best;
    }

    private static void WriteLumaMode16(ref BoolEncoder enc, int ymode)
    {
        switch (ymode)
        {
            case 0: enc.PutBit(0, 156); enc.PutBit(0, 163); break; // DC
            case 2: enc.PutBit(0, 156); enc.PutBit(1, 163); break; // V
            case 3: enc.PutBit(1, 156); enc.PutBit(0, 128); break; // H
            default: enc.PutBit(1, 156); enc.PutBit(1, 128); break; // TM
        }
    }

    private static void WriteChromaMode(ref BoolEncoder enc, int uvmode)
    {
        switch (uvmode)
        {
            case 0: enc.PutBit(0, 142); break; // DC
            case 2: enc.PutBit(1, 142); enc.PutBit(0, 114); break; // V
            case 3: enc.PutBit(1, 142); enc.PutBit(1, 114); enc.PutBit(0, 183); break; // H
            default: enc.PutBit(1, 142); enc.PutBit(1, 114); enc.PutBit(1, 183); break; // TM
        }
    }

    // Round a DCT coefficient to a quantized level (dequantized on decode as level*q).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short QuantizeLevel(int coeff, int q)
    {
        if (q <= 0)
        {
            return 0;
        }

        int a = coeff < 0 ? -coeff : coeff;
        int level = (a + (q >> 1)) / q;
        if (level > 2047)
        {
            level = 2047; // fits the cat6 token range
        }

        return (short)(coeff < 0 ? -level : level);
    }

    // Encodes one 4x4 block of quantized levels as VP8 coefficient tokens — the exact
    // inverse of GetCoeffs. Returns whether the block had any non-zero level (for the
    // neighbour context). Levels are in natural order; tokens are emitted in scan order.
    private static bool EncodeBlock(ref BoolEncoder enc, byte[] probs, int type, int ctx, int firstN, short[] levels)
    {
        int last = firstN - 1;
        for (int n = firstN; n < 16; n++)
        {
            if (levels[ZigZag[n]] != 0)
            {
                last = n;
            }
        }

        int ctxCur = ctx;
        int n2 = firstN;
        while (true)
        {
            int off = type * 264 + Bands[n2] * 33 + ctxCur * 11;
            if (n2 > last)
            {
                enc.PutBit(0, probs[off + 0]); // EOB
                break;
            }

            enc.PutBit(1, probs[off + 0]); // not EOB
            while (levels[ZigZag[n2]] == 0)
            {
                enc.PutBit(0, probs[off + 1]); // zero coefficient
                n2++;
                off = type * 264 + Bands[n2] * 33; // context 0 during the run
            }

            enc.PutBit(1, probs[off + 1]); // non-zero
            int c = levels[ZigZag[n2]];
            int a = c < 0 ? -c : c;
            if (a == 1)
            {
                enc.PutBit(0, probs[off + 2]);
                ctxCur = 1;
            }
            else
            {
                enc.PutBit(1, probs[off + 2]);
                EncodeLargeValue(ref enc, probs, off, a);
                ctxCur = 2;
            }

            enc.PutBit(c < 0 ? 1 : 0, 128); // sign
            n2++;
            if (n2 == 16)
            {
                break;
            }
        }

        return last >= firstN;
    }

    // Emits the magnitude of a coefficient > 1 — inverse of GetLargeValue.
    private static void EncodeLargeValue(ref BoolEncoder enc, byte[] probs, int p, int a)
    {
        if (a <= 4)
        {
            enc.PutBit(0, probs[p + 3]);
            if (a == 2)
            {
                enc.PutBit(0, probs[p + 4]);
            }
            else
            {
                enc.PutBit(1, probs[p + 4]);
                enc.PutBit(a - 3, probs[p + 5]); // 3->0, 4->1
            }

            return;
        }

        if (a <= 10)
        {
            enc.PutBit(1, probs[p + 3]);
            enc.PutBit(0, probs[p + 6]);
            if (a <= 6)
            {
                enc.PutBit(0, probs[p + 7]);
                enc.PutBit(a - 5, 159); // 5->0, 6->1
            }
            else
            {
                enc.PutBit(1, probs[p + 7]);
                enc.PutBit((a - 7) >> 1, 165);
                enc.PutBit((a - 7) & 1, 145);
            }

            return;
        }

        // Categories 3..6 (a >= 11).
        enc.PutBit(1, probs[p + 3]);
        enc.PutBit(1, probs[p + 6]);
        int cat = a <= 18 ? 0 : a <= 34 ? 1 : a <= 66 ? 2 : 3;
        int bit1 = cat >> 1;
        int bit0 = cat & 1;
        enc.PutBit(bit1, probs[p + 8]);
        enc.PutBit(bit0, probs[p + 9 + bit1]);
        int extra = a - (3 + (8 << cat));
        byte[] tab = Cat3456[cat];
        for (int t = 0; t < tab.Length; t++)
        {
            enc.PutBit((extra >> (tab.Length - 1 - t)) & 1, tab[t]);
        }
    }

    // Transforms, quantizes, encodes and reconstructs the four 4x4 blocks of one 8x8 chroma
    // plane (already predicted into `pb`), updating the neighbour non-zero contexts.
    private static void EncodeChromaPlane(byte[] pb, byte[] plane, int stride, int mbCol, int mbRow, int qdc, int qac, short[][] lvl, int[] aboveNz, int[] leftNz, ref BoolEncoder enc, short[] deq, short[] residual)
    {
        const int Off = Bps + 8;
        short[] dct = new short[16];
        for (int y = 0; y < 2; y++)
        {
            int l = leftNz[y];
            for (int x = 0; x < 2; x++)
            {
                int n = y * 2 + x;
                int dstIdx = Off + y * 4 * Bps + x * 4;
                int ox = mbCol * 8 + x * 4;
                int oy = mbRow * 8 + y * 4;
                ForwardDct(plane, stride, ox, oy, pb, dstIdx, dct);
                Array.Clear(lvl[n], 0, 16);
                lvl[n][0] = QuantizeLevel(dct[0], qdc);
                for (int j = 1; j < 16; j++)
                {
                    lvl[n][j] = QuantizeLevel(dct[j], qac);
                }

                int ctx = l + aboveNz[mbCol * 2 + x];
                bool nz = EncodeBlock(ref enc, DefaultCoeffProbs, 2, ctx, 0, lvl[n]);
                int f = nz ? 1 : 0;
                l = f;
                aboveNz[mbCol * 2 + x] = f;

                deq[0] = (short)(lvl[n][0] * qdc);
                for (int j = 1; j < 16; j++)
                {
                    deq[j] = (short)(lvl[n][j] * qac);
                }

                AddResidual(pb, dstIdx, deq, residual);
            }

            leftNz[y] = l;
        }
    }

    // Forward 4x4 DCT of (source - prediction), bit-matched to libwebp's FTransform so the
    // decoder's inverse transform recovers it. `src` is read from an image plane; `ref` is
    // the predicted block in a working buffer (Bps stride). Output is natural (raster) order.
    private static void ForwardDct(byte[] src, int srcStride, int sx, int sy, byte[] refBuf, int refIdx, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int sBase = (sy + i) * srcStride + sx;
            int rBase = refIdx + i * Bps;
            int d0 = src[sBase + 0] - refBuf[rBase + 0];
            int d1 = src[sBase + 1] - refBuf[rBase + 1];
            int d2 = src[sBase + 2] - refBuf[rBase + 2];
            int d3 = src[sBase + 3] - refBuf[rBase + 3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            tmp[0 + i * 4] = (a0 + a1) * 8;
            tmp[1 + i * 4] = (a2 * 2217 + a3 * 5352 + 1812) >> 9;
            tmp[2 + i * 4] = (a0 - a1) * 8;
            tmp[3 + i * 4] = (a3 * 2217 - a2 * 5352 + 937) >> 9;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[12 + i];
            int a1 = tmp[4 + i] + tmp[8 + i];
            int a2 = tmp[4 + i] - tmp[8 + i];
            int a3 = tmp[0 + i] - tmp[12 + i];
            output[0 + i] = (short)((a0 + a1 + 7) >> 4);
            output[4 + i] = (short)(((a2 * 2217 + a3 * 5352 + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[12 + i] = (short)((a3 * 2217 - a2 * 5352 + 51000) >> 16);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Prediction
    // ═══════════════════════════════════════════════════════════════════

    // ── VP8 keyframe intra prediction (RFC 6386 §12, ported from libwebp dsp/dec.c) ──
    // Mode enums match libwebp's predictor tables exactly:
    //   16x16 luma / 8x8 chroma: DC=0, TM=1, VE(vertical)=2, HE(horizontal)=3,
    //                            plus edge DC variants 4=NoTop, 5=NoLeft, 6=NoTopLeft.
    //   4x4 B_PRED submodes:     B_DC=0, B_TM=1, B_VE=2, B_HE=3, B_RD=4, B_VR=5,
    //                            B_LD=6, B_VL=7, B_HD=8, B_HU=9.
    // Predictors operate on a per-macroblock working buffer with a fixed Bps stride;
    // `dst` is the buffer index of the block's pixel (0,0). Neighbours are read at
    // dst-Bps (top row), dst-1 (left column) and dst-1-Bps (top-left corner).

    // ── In-loop deblocking filter primitives (RFC 6386 §15, ported from libwebp) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip1(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sclip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sclip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs0(int v) => v < 0 ? -v : v;

    private static void DoFilter2(byte[] b, int p, int step)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        int a = 3 * (q0 - p0) + Sclip1(p1 - q1);
        int a1 = Sclip2((a + 4) >> 3);
        int a2 = Sclip2((a + 3) >> 3);
        b[p - step] = (byte)Clip1(p0 + a2);
        b[p] = (byte)Clip1(q0 - a1);
    }

    private static void DoFilter4(byte[] b, int p, int step)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        int a = 3 * (q0 - p0);
        int a1 = Sclip2((a + 4) >> 3);
        int a2 = Sclip2((a + 3) >> 3);
        int a3 = (a1 + 1) >> 1;
        b[p - 2 * step] = (byte)Clip1(p1 + a3);
        b[p - step] = (byte)Clip1(p0 + a2);
        b[p] = (byte)Clip1(q0 - a1);
        b[p + step] = (byte)Clip1(q1 - a3);
    }

    private static void DoFilter6(byte[] b, int p, int step)
    {
        int p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step];
        int q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step];
        int a = Sclip1(3 * (q0 - p0) + Sclip1(p1 - q1));
        int a1 = (27 * a + 63) >> 7;
        int a2 = (18 * a + 63) >> 7;
        int a3 = (9 * a + 63) >> 7;
        b[p - 3 * step] = (byte)Clip1(p2 + a3);
        b[p - 2 * step] = (byte)Clip1(p1 + a2);
        b[p - step] = (byte)Clip1(p0 + a1);
        b[p] = (byte)Clip1(q0 - a1);
        b[p + step] = (byte)Clip1(q1 - a2);
        b[p + 2 * step] = (byte)Clip1(q2 - a3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hev(byte[] b, int p, int step, int thresh)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        return Abs0(p1 - p0) > thresh || Abs0(q1 - q0) > thresh;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NeedsFilterSimple(byte[] b, int p, int step, int t)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        return 4 * Abs0(p0 - q0) + Abs0(p1 - q1) <= t;
    }

    private static bool NeedsFilter2(byte[] b, int p, int step, int t, int it)
    {
        int p3 = b[p - 4 * step], p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step];
        int q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step], q3 = b[p + 3 * step];
        if (4 * Abs0(p0 - q0) + Abs0(p1 - q1) > t)
        {
            return false;
        }

        return Abs0(p3 - p2) <= it && Abs0(p2 - p1) <= it && Abs0(p1 - p0) <= it &&
               Abs0(q3 - q2) <= it && Abs0(q2 - q1) <= it && Abs0(q1 - q0) <= it;
    }

    // Complex filter along a MB or inner edge: `hstride` steps across the edge, `vstride`
    // walks along it; `inner` selects the 4-tap inner-edge kernel over the 6-tap MB kernel.
    private static void FilterLoop(byte[] b, int p, int hstride, int vstride, int size, int thresh, int ithresh, int hevThresh, bool inner)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < size; i++)
        {
            if (NeedsFilter2(b, p, hstride, thresh2, ithresh))
            {
                if (Hev(b, p, hstride, hevThresh))
                {
                    DoFilter2(b, p, hstride);
                }
                else if (inner)
                {
                    DoFilter4(b, p, hstride);
                }
                else
                {
                    DoFilter6(b, p, hstride);
                }
            }

            p += vstride;
        }
    }

    private static void SimpleVFilter16(byte[] b, int p, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilterSimple(b, p + i, stride, thresh2))
            {
                DoFilter2(b, p + i, stride);
            }
        }
    }

    private static void SimpleHFilter16(byte[] b, int p, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; i++)
        {
            if (NeedsFilterSimple(b, p + i * stride, 1, thresh2))
            {
                DoFilter2(b, p + i * stride, 1);
            }
        }
    }

    private static void SimpleVFilter16Inner(byte[] b, int p, int stride, int thresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            SimpleVFilter16(b, p + 4 * k * stride, stride, thresh);
        }
    }

    private static void SimpleHFilter16Inner(byte[] b, int p, int stride, int thresh)
    {
        for (int k = 1; k <= 3; k++)
        {
            SimpleHFilter16(b, p + 4 * k, stride, thresh);
        }
    }

    // Inverse-transforms a 4x4 coefficient block and adds it to the predicted samples.
    private static void AddResidual(byte[] b, int dst, short[] coeffs, short[] residual)
    {
        InverseDct4x4(coeffs, residual);
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                int idx = dst + r * Bps + c;
                b[idx] = ClampByte(b[idx] + residual[r * 4 + c]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    // Edge-DC selection: DC prediction uses the NoTop/NoLeft/NoTopLeft variant at the
    // frame border so the synthetic 127/129 border samples are not averaged in.
    private static int CheckMode(int mbCol, int mbRow, int mode)
    {
        if (mode == 0)
        {
            if (mbCol == 0)
            {
                return mbRow == 0 ? 6 : 5; // NoTopLeft : NoLeft
            }

            return mbRow == 0 ? 4 : 0;     // NoTop : DC
        }

        return mode;
    }

    private static void PredBlock(byte[] b, int dst, int size, int f)
    {
        switch (f)
        {
            case 1: PredTrueMotion(b, dst, size); break;
            case 2: PredVertical(b, dst, size); break;
            case 3: PredHorizontal(b, dst, size); break;
            case 0: PredDc(b, dst, size, true, true); break;
            case 4: PredDc(b, dst, size, false, true); break;  // top not available
            case 5: PredDc(b, dst, size, true, false); break;  // left not available
            default: PredDc(b, dst, size, false, false); break; // neither
        }
    }

    private static void PredTrueMotion(byte[] b, int dst, int size)
    {
        int tl = b[dst - Bps - 1];
        for (int y = 0; y < size; y++)
        {
            int left = b[dst + y * Bps - 1];
            int row = dst + y * Bps;
            for (int x = 0; x < size; x++)
            {
                int v = left + b[dst - Bps + x] - tl;
                b[row + x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }
    }

    private static void PredVertical(byte[] b, int dst, int size)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                b[dst + y * Bps + x] = b[dst - Bps + x];
            }
        }
    }

    private static void PredHorizontal(byte[] b, int dst, int size)
    {
        for (int y = 0; y < size; y++)
        {
            byte l = b[dst + y * Bps - 1];
            for (int x = 0; x < size; x++)
            {
                b[dst + y * Bps + x] = l;
            }
        }
    }

    private static void PredDc(byte[] b, int dst, int size, bool top, bool left)
    {
        int dc;
        if (top && left)
        {
            int sum = size;
            for (int i = 0; i < size; i++)
            {
                sum += b[dst - Bps + i] + b[dst + i * Bps - 1];
            }

            dc = sum >> (size == 16 ? 5 : 4);
        }
        else if (top)
        {
            int sum = size / 2;
            for (int i = 0; i < size; i++)
            {
                sum += b[dst - Bps + i];
            }

            dc = sum >> (size == 16 ? 4 : 3);
        }
        else if (left)
        {
            int sum = size / 2;
            for (int i = 0; i < size; i++)
            {
                sum += b[dst + i * Bps - 1];
            }

            dc = sum >> (size == 16 ? 4 : 3);
        }
        else
        {
            dc = 0x80;
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                b[dst + y * Bps + x] = (byte)dc;
            }
        }
    }

    private static void Pred4(byte[] b, int dst, int mode)
    {
        switch (mode)
        {
            case 0: Dc4(b, dst); break;
            case 1: PredTrueMotion(b, dst, 4); break;
            case 2: Ve4(b, dst); break;
            case 3: He4(b, dst); break;
            case 4: Rd4(b, dst); break;
            case 5: Vr4(b, dst); break;
            case 6: Ld4(b, dst); break;
            case 7: Vl4(b, dst); break;
            case 8: Hd4(b, dst); break;
            default: Hu4(b, dst); break;
        }
    }

    private static void Dc4(byte[] b, int dst)
    {
        int dc = 4;
        for (int i = 0; i < 4; i++)
        {
            dc += b[dst - Bps + i] + b[dst - 1 + i * Bps];
        }

        dc >>= 3;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                b[dst + y * Bps + x] = (byte)dc;
            }
        }
    }

    private static void Ve4(byte[] b, int dst)
    {
        int t = dst - Bps;
        byte v0 = Avg3(b[t - 1], b[t + 0], b[t + 1]);
        byte v1 = Avg3(b[t + 0], b[t + 1], b[t + 2]);
        byte v2 = Avg3(b[t + 1], b[t + 2], b[t + 3]);
        byte v3 = Avg3(b[t + 2], b[t + 3], b[t + 4]);
        for (int y = 0; y < 4; y++)
        {
            int r = dst + y * Bps;
            b[r] = v0; b[r + 1] = v1; b[r + 2] = v2; b[r + 3] = v3;
        }
    }

    private static void He4(byte[] b, int dst)
    {
        int a = b[dst - 1 - Bps], bb = b[dst - 1], c = b[dst - 1 + Bps];
        int d = b[dst - 1 + 2 * Bps], e = b[dst - 1 + 3 * Bps];
        Fill4(b, dst + 0 * Bps, Avg3(a, bb, c));
        Fill4(b, dst + 1 * Bps, Avg3(bb, c, d));
        Fill4(b, dst + 2 * Bps, Avg3(c, d, e));
        Fill4(b, dst + 3 * Bps, Avg3(d, e, e));
    }

    private static void Fill4(byte[] b, int row, byte v)
    {
        b[row] = v; b[row + 1] = v; b[row + 2] = v; b[row + 3] = v;
    }

    private static void Rd4(byte[] b, int dst)
    {
        int i = b[dst - 1 + 0 * Bps], j = b[dst - 1 + 1 * Bps], k = b[dst - 1 + 2 * Bps], l = b[dst - 1 + 3 * Bps];
        int x = b[dst - 1 - Bps], a = b[dst - Bps], bb = b[dst + 1 - Bps], c = b[dst + 2 - Bps], d = b[dst + 3 - Bps];
        D(b, dst, 0, 3, Avg3(j, k, l));
        D(b, dst, 1, 3, D(b, dst, 0, 2, Avg3(i, j, k)));
        D(b, dst, 2, 3, D(b, dst, 1, 2, D(b, dst, 0, 1, Avg3(x, i, j))));
        D(b, dst, 3, 3, D(b, dst, 2, 2, D(b, dst, 1, 1, D(b, dst, 0, 0, Avg3(a, x, i)))));
        D(b, dst, 3, 2, D(b, dst, 2, 1, D(b, dst, 1, 0, Avg3(bb, a, x))));
        D(b, dst, 3, 1, D(b, dst, 2, 0, Avg3(c, bb, a)));
        D(b, dst, 3, 0, Avg3(d, c, bb));
    }

    private static void Ld4(byte[] b, int dst)
    {
        int a = b[dst - Bps], bb = b[dst + 1 - Bps], c = b[dst + 2 - Bps], d = b[dst + 3 - Bps];
        int e = b[dst + 4 - Bps], f = b[dst + 5 - Bps], g = b[dst + 6 - Bps], h = b[dst + 7 - Bps];
        D(b, dst, 0, 0, Avg3(a, bb, c));
        D(b, dst, 1, 0, D(b, dst, 0, 1, Avg3(bb, c, d)));
        D(b, dst, 2, 0, D(b, dst, 1, 1, D(b, dst, 0, 2, Avg3(c, d, e))));
        D(b, dst, 3, 0, D(b, dst, 2, 1, D(b, dst, 1, 2, D(b, dst, 0, 3, Avg3(d, e, f)))));
        D(b, dst, 3, 1, D(b, dst, 2, 2, D(b, dst, 1, 3, Avg3(e, f, g))));
        D(b, dst, 3, 2, D(b, dst, 2, 3, Avg3(f, g, h)));
        D(b, dst, 3, 3, Avg3(g, h, h));
    }

    private static void Vr4(byte[] b, int dst)
    {
        int i = b[dst - 1 + 0 * Bps], j = b[dst - 1 + 1 * Bps], k = b[dst - 1 + 2 * Bps];
        int x = b[dst - 1 - Bps], a = b[dst - Bps], bb = b[dst + 1 - Bps], c = b[dst + 2 - Bps], d = b[dst + 3 - Bps];
        D(b, dst, 0, 0, D(b, dst, 1, 2, Avg2(x, a)));
        D(b, dst, 1, 0, D(b, dst, 2, 2, Avg2(a, bb)));
        D(b, dst, 2, 0, D(b, dst, 3, 2, Avg2(bb, c)));
        D(b, dst, 3, 0, Avg2(c, d));
        D(b, dst, 0, 3, Avg3(k, j, i));
        D(b, dst, 0, 2, Avg3(j, i, x));
        D(b, dst, 0, 1, D(b, dst, 1, 3, Avg3(i, x, a)));
        D(b, dst, 1, 1, D(b, dst, 2, 3, Avg3(x, a, bb)));
        D(b, dst, 2, 1, D(b, dst, 3, 3, Avg3(a, bb, c)));
        D(b, dst, 3, 1, Avg3(bb, c, d));
    }

    private static void Vl4(byte[] b, int dst)
    {
        int a = b[dst - Bps], bb = b[dst + 1 - Bps], c = b[dst + 2 - Bps], d = b[dst + 3 - Bps];
        int e = b[dst + 4 - Bps], f = b[dst + 5 - Bps], g = b[dst + 6 - Bps], h = b[dst + 7 - Bps];
        D(b, dst, 0, 0, Avg2(a, bb));
        D(b, dst, 1, 0, D(b, dst, 0, 2, Avg2(bb, c)));
        D(b, dst, 2, 0, D(b, dst, 1, 2, Avg2(c, d)));
        D(b, dst, 3, 0, D(b, dst, 2, 2, Avg2(d, e)));
        D(b, dst, 0, 1, Avg3(a, bb, c));
        D(b, dst, 1, 1, D(b, dst, 0, 3, Avg3(bb, c, d)));
        D(b, dst, 2, 1, D(b, dst, 1, 3, Avg3(c, d, e)));
        D(b, dst, 3, 1, D(b, dst, 2, 3, Avg3(d, e, f)));
        D(b, dst, 3, 2, Avg3(e, f, g));
        D(b, dst, 3, 3, Avg3(f, g, h));
    }

    private static void Hu4(byte[] b, int dst)
    {
        int i = b[dst - 1 + 0 * Bps], j = b[dst - 1 + 1 * Bps], k = b[dst - 1 + 2 * Bps], l = b[dst - 1 + 3 * Bps];
        D(b, dst, 0, 0, Avg2(i, j));
        D(b, dst, 2, 0, D(b, dst, 0, 1, Avg2(j, k)));
        D(b, dst, 2, 1, D(b, dst, 0, 2, Avg2(k, l)));
        D(b, dst, 1, 0, Avg3(i, j, k));
        D(b, dst, 3, 0, D(b, dst, 1, 1, Avg3(j, k, l)));
        D(b, dst, 3, 1, D(b, dst, 1, 2, Avg3(k, l, l)));
        byte ll = (byte)l;
        D(b, dst, 3, 2, ll); D(b, dst, 2, 2, ll); D(b, dst, 0, 3, ll);
        D(b, dst, 1, 3, ll); D(b, dst, 2, 3, ll); D(b, dst, 3, 3, ll);
    }

    private static void Hd4(byte[] b, int dst)
    {
        int i = b[dst - 1 + 0 * Bps], j = b[dst - 1 + 1 * Bps], k = b[dst - 1 + 2 * Bps], l = b[dst - 1 + 3 * Bps];
        int x = b[dst - 1 - Bps], a = b[dst - Bps], bb = b[dst + 1 - Bps], c = b[dst + 2 - Bps];
        D(b, dst, 0, 0, D(b, dst, 2, 1, Avg2(i, x)));
        D(b, dst, 0, 1, D(b, dst, 2, 2, Avg2(j, i)));
        D(b, dst, 0, 2, D(b, dst, 2, 3, Avg2(k, j)));
        D(b, dst, 0, 3, Avg2(l, k));
        D(b, dst, 3, 0, Avg3(a, bb, c));
        D(b, dst, 2, 0, Avg3(x, a, bb));
        D(b, dst, 1, 0, D(b, dst, 3, 1, Avg3(i, x, a)));
        D(b, dst, 1, 1, D(b, dst, 3, 2, Avg3(j, i, x)));
        D(b, dst, 1, 2, D(b, dst, 3, 3, Avg3(k, j, i)));
        D(b, dst, 1, 3, Avg3(l, k, j));
    }

    // Writes one pixel of a 4x4 block and returns the value (so shared results can chain).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte D(byte[] b, int dst, int x, int y, byte v)
    {
        b[dst + x + y * Bps] = v;
        return v;
    }

    // Keyframe 4x4 intra submode tree (RFC 6386 §11.5), context = [above][left] submodes.
    private static int DecodeBMode(ref BoolDecoder bd, int above, int left)
    {
        int p = (above * 10 + left) * 9;
        if (bd.ReadBool(KBModesProba[p + 0]) == 0)
        {
            return 0; // B_DC
        }

        if (bd.ReadBool(KBModesProba[p + 1]) == 0)
        {
            return 1; // B_TM
        }

        if (bd.ReadBool(KBModesProba[p + 2]) == 0)
        {
            return 2; // B_VE
        }

        if (bd.ReadBool(KBModesProba[p + 3]) == 0)
        {
            if (bd.ReadBool(KBModesProba[p + 4]) == 0)
            {
                return 3; // B_HE
            }

            return bd.ReadBool(KBModesProba[p + 5]) == 0 ? 4 : 5; // B_RD : B_VR
        }

        if (bd.ReadBool(KBModesProba[p + 6]) == 0)
        {
            return 6; // B_LD
        }

        if (bd.ReadBool(KBModesProba[p + 7]) == 0)
        {
            return 7; // B_VL
        }

        return bd.ReadBool(KBModesProba[p + 8]) == 0 ? 8 : 9; // B_HD : B_HU
    }

    // ═══════════════════════════════════════════════════════════════════
    // 4x4 DCT / IDCT and WHT
    // ═══════════════════════════════════════════════════════════════════

    // Inverse 4x4 DCT, bit-exact with libwebp's TransformOne_C (RFC 6386 §14.3). Two
    // transposing passes; the +4 rounder is applied only in the second (horizontal) pass.
    // 85627 == ((a*20091)>>16)+a folded into one multiply. Outputs the residual (row-major).
    private static void InverseDct4x4(short[] input, short[] output)
    {
        Span<int> c = stackalloc int[16];

        // Vertical pass: input column i → intermediate row i.
        for (int i = 0; i < 4; i++)
        {
            int a = input[i] + input[i + 8];
            int b = input[i] - input[i + 8];
            int cc = ((input[i + 4] * 35468) >> 16) - ((input[i + 12] * 85627) >> 16);
            int d = ((input[i + 4] * 85627) >> 16) + ((input[i + 12] * 35468) >> 16);
            c[i * 4 + 0] = a + d;
            c[i * 4 + 1] = b + cc;
            c[i * 4 + 2] = b - cc;
            c[i * 4 + 3] = a - d;
        }

        // Horizontal pass: intermediate column i → output row i.
        for (int i = 0; i < 4; i++)
        {
            int dc = c[i] + 4;
            int a = dc + c[i + 8];
            int b = dc - c[i + 8];
            int cc = ((c[i + 4] * 35468) >> 16) - ((c[i + 12] * 85627) >> 16);
            int d = ((c[i + 4] * 85627) >> 16) + ((c[i + 12] * 35468) >> 16);
            output[i * 4 + 0] = (short)((a + d) >> 3);
            output[i * 4 + 1] = (short)((b + cc) >> 3);
            output[i * 4 + 2] = (short)((b - cc) >> 3);
            output[i * 4 + 3] = (short)((a - d) >> 3);
        }
    }

    private static void InverseWht(short[] input, short[] output)
    {
        Span<int> temp = stackalloc int[16];
        for (int i = 0;i < 4;i++)
        {
            int a = input[i * 4 + 0] + input[i * 4 + 3];
            int b = input[i * 4 + 1] + input[i * 4 + 2];
            int c = input[i * 4 + 1] - input[i * 4 + 2];
            int d = input[i * 4 + 0] - input[i * 4 + 3];
            temp[i * 4 + 0] = a + b;
            temp[i * 4 + 1] = c + d;
            temp[i * 4 + 2] = a - b;
            temp[i * 4 + 3] = d - c;
        }
        for (int i = 0;i < 4;i++)
        {
            int a = temp[0 * 4 + i] + temp[3 * 4 + i];
            int b = temp[1 * 4 + i] + temp[2 * 4 + i];
            int c = temp[1 * 4 + i] - temp[2 * 4 + i];
            int d = temp[0 * 4 + i] - temp[3 * 4 + i];
            output[0 * 4 + i] = (short)((a + b + 3) >> 3);
            output[1 * 4 + i] = (short)((c + d + 3) >> 3);
            output[2 * 4 + i] = (short)((a - b + 3) >> 3);
            output[3 * 4 + i] = (short)((d - c + 3) >> 3);
        }
    }

    // Forward Walsh-Hadamard transform of the 16 luma DC coefficients (raster order),
    // bit-matched to libwebp's FTransformWHT so InverseWht recovers it on decode.
    private static void ForwardWht(short[] input, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i * 4 + 0] + input[i * 4 + 2];
            int a1 = input[i * 4 + 1] + input[i * 4 + 3];
            int a2 = input[i * 4 + 1] - input[i * 4 + 3];
            int a3 = input[i * 4 + 0] - input[i * 4 + 2];
            tmp[0 + i * 4] = a0 + a1;
            tmp[1 + i * 4] = a3 + a2;
            tmp[2 + i * 4] = a3 - a2;
            tmp[3 + i * 4] = a0 - a1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a0 = tmp[0 + i] + tmp[8 + i];
            int a1 = tmp[4 + i] + tmp[12 + i];
            int a2 = tmp[4 + i] - tmp[12 + i];
            int a3 = tmp[0 + i] - tmp[8 + i];
            output[0 + i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Utility
    // ═══════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ClampByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // libwebp's VP8Clip8: descale a YUV_FIX2 (<<6) fixed-point value to a clamped byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte YuvClip8(int v) => (byte)((v & ~((256 << 6) - 1)) == 0 ? v >> 6 : v < 0 ? 0 : 255);

    // "Fancy" bilinear 4:2:0 chroma upsampling for one pair of output rows, ported from
    // libwebp's UPSAMPLE_FUNC (dsp/upsampling.c). Each output pixel blends the four nearest
    // chroma samples with 9:3:3:1 weights. `topRow`/`botRow` receive the two output rows;
    // when doBottom is false only the top row is written (used for the very first row).
    private static void UpsampleUvPair(byte[] uPlane, byte[] vPlane, int uvStride, int cyTop, int cyCur, byte[] uFull, byte[] vFull, int topRow, int botRow, int width, bool doBottom)
    {
        int tuBase = cyTop * uvStride;
        int cuBase = cyCur * uvStride;
        int toOff = topRow * width;
        int boOff = botRow * width;
        int lastPair = (width - 1) >> 1;

        int tlU = uPlane[tuBase], tlV = vPlane[tuBase];
        int lU = uPlane[cuBase], lV = vPlane[cuBase];

        uFull[toOff] = (byte)((3 * tlU + lU + 2) >> 2);
        vFull[toOff] = (byte)((3 * tlV + lV + 2) >> 2);
        if (doBottom)
        {
            uFull[boOff] = (byte)((3 * lU + tlU + 2) >> 2);
            vFull[boOff] = (byte)((3 * lV + tlV + 2) >> 2);
        }

        for (int x = 1; x <= lastPair; x++)
        {
            int tU = uPlane[tuBase + x], tV = vPlane[tuBase + x];
            int cU = uPlane[cuBase + x], cV = vPlane[cuBase + x];

            int avgU = tlU + tU + lU + cU + 8;
            int d12U = (avgU + 2 * (tU + lU)) >> 3;
            int d03U = (avgU + 2 * (tlU + cU)) >> 3;
            int avgV = tlV + tV + lV + cV + 8;
            int d12V = (avgV + 2 * (tV + lV)) >> 3;
            int d03V = (avgV + 2 * (tlV + cV)) >> 3;

            uFull[toOff + 2 * x - 1] = (byte)((d12U + tlU) >> 1);
            vFull[toOff + 2 * x - 1] = (byte)((d12V + tlV) >> 1);
            uFull[toOff + 2 * x] = (byte)((d03U + tU) >> 1);
            vFull[toOff + 2 * x] = (byte)((d03V + tV) >> 1);
            if (doBottom)
            {
                uFull[boOff + 2 * x - 1] = (byte)((d03U + lU) >> 1);
                vFull[boOff + 2 * x - 1] = (byte)((d03V + lV) >> 1);
                uFull[boOff + 2 * x] = (byte)((d12U + cU) >> 1);
                vFull[boOff + 2 * x] = (byte)((d12V + cV) >> 1);
            }

            tlU = tU; lU = cU; tlV = tV; lV = cV;
        }

        if ((width & 1) == 0)
        {
            int lastX = width - 1;
            uFull[toOff + lastX] = (byte)((3 * tlU + lU + 2) >> 2);
            vFull[toOff + lastX] = (byte)((3 * tlV + lV + 2) >> 2);
            if (doBottom)
            {
                uFull[boOff + lastX] = (byte)((3 * lU + tlU + 2) >> 2);
                vFull[boOff + lastX] = (byte)((3 * lV + tlV + 2) >> 2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp128(int v) => v < 0 ? 0 : v > 127 ? 127 : v;
}
