using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/common/resize.c's non-normative scaler (av1_resize_and_extend_frame_nonnormative:
// av1_resize_plane / highbd_resize_plane, resize_multistep, down2_symeven / down2_symodd, interpolate). The 8-bit and
// high bit depth versions differ only in the pixel clip; the C splits (initial / middle / end parts) are the clamped
// index forms written out, so the clamped loops here are the same arithmetic.
internal static partial class AomResize
{
    private static readonly short[] Filters500 =
    {
        -3, 0, 35, 64, 35, 0, -3, 0,
        -3, 0, 34, 64, 36, 0, -3, 0,
        -3, -1, 34, 64, 36, 1, -3, 0,
        -3, -1, 33, 64, 37, 1, -3, 0,
        -3, -1, 32, 64, 38, 1, -3, 0,
        -3, -1, 31, 64, 39, 1, -3, 0,
        -3, -1, 31, 63, 39, 2, -3, 0,
        -2, -2, 30, 63, 40, 2, -3, 0,
        -2, -2, 29, 63, 41, 2, -3, 0,
        -2, -2, 29, 63, 41, 3, -4, 0,
        -2, -2, 28, 63, 42, 3, -4, 0,
        -2, -2, 27, 63, 43, 3, -4, 0,
        -2, -3, 27, 63, 43, 4, -4, 0,
        -2, -3, 26, 62, 44, 5, -4, 0,
        -2, -3, 25, 62, 45, 5, -4, 0,
        -2, -3, 25, 62, 45, 5, -4, 0,
        -2, -3, 24, 62, 46, 5, -4, 0,
        -2, -3, 23, 61, 47, 6, -4, 0,
        -2, -3, 23, 61, 47, 6, -4, 0,
        -2, -3, 22, 61, 48, 7, -4, -1,
        -2, -3, 21, 60, 49, 7, -4, 0,
        -1, -4, 20, 60, 49, 8, -4, 0,
        -1, -4, 20, 60, 50, 8, -4, -1,
        -1, -4, 19, 59, 51, 9, -4, -1,
        -1, -4, 19, 59, 51, 9, -4, -1,
        -1, -4, 18, 58, 52, 10, -4, -1,
        -1, -4, 17, 58, 52, 11, -4, -1,
        -1, -4, 16, 58, 53, 11, -4, -1,
        -1, -4, 16, 57, 53, 12, -4, -1,
        -1, -4, 15, 57, 54, 12, -4, -1,
        -1, -4, 15, 56, 54, 13, -4, -1,
        -1, -4, 14, 56, 55, 13, -4, -1,
        -1, -4, 14, 55, 55, 14, -4, -1,
        -1, -4, 13, 55, 56, 14, -4, -1,
        -1, -4, 13, 54, 56, 15, -4, -1,
        -1, -4, 12, 54, 57, 15, -4, -1,
        -1, -4, 12, 53, 57, 16, -4, -1,
        -1, -4, 11, 53, 58, 16, -4, -1,
        -1, -4, 11, 52, 58, 17, -4, -1,
        -1, -4, 10, 52, 58, 18, -4, -1,
        -1, -4, 9, 51, 59, 19, -4, -1,
        -1, -4, 9, 51, 59, 19, -4, -1,
        -1, -4, 8, 50, 60, 20, -4, -1,
        0, -4, 8, 49, 60, 20, -4, -1,
        0, -4, 7, 49, 60, 21, -3, -2,
        -1, -4, 7, 48, 61, 22, -3, -2,
        0, -4, 6, 47, 61, 23, -3, -2,
        0, -4, 6, 47, 61, 23, -3, -2,
        0, -4, 5, 46, 62, 24, -3, -2,
        0, -4, 5, 45, 62, 25, -3, -2,
        0, -4, 5, 45, 62, 25, -3, -2,
        0, -4, 5, 44, 62, 26, -3, -2,
        0, -4, 4, 43, 63, 27, -3, -2,
        0, -4, 3, 43, 63, 27, -2, -2,
        0, -4, 3, 42, 63, 28, -2, -2,
        0, -4, 3, 41, 63, 29, -2, -2,
        0, -3, 2, 41, 63, 29, -2, -2,
        0, -3, 2, 40, 63, 30, -2, -2,
        0, -3, 2, 39, 63, 31, -1, -3,
        0, -3, 1, 39, 64, 31, -1, -3,
        0, -3, 1, 38, 64, 32, -1, -3,
        0, -3, 1, 37, 64, 33, -1, -3,
        0, -3, 1, 36, 64, 34, -1, -3,
        0, -3, 0, 36, 64, 34, 0, -3,
    };
    private static readonly short[] Filters625 =
    {
        -1, -8, 33, 80, 33, -8, -1, 0,
        -1, -8, 31, 80, 34, -8, -1, 1,
        -1, -8, 30, 80, 35, -8, -1, 1,
        -1, -8, 29, 80, 36, -7, -2, 1,
        -1, -8, 28, 80, 37, -7, -2, 1,
        -1, -8, 27, 80, 38, -7, -2, 1,
        0, -8, 26, 79, 39, -7, -2, 1,
        0, -8, 25, 79, 40, -7, -2, 1,
        0, -8, 24, 79, 41, -7, -2, 1,
        0, -8, 23, 78, 42, -6, -2, 1,
        0, -8, 22, 78, 43, -6, -2, 1,
        0, -8, 21, 78, 44, -6, -2, 1,
        0, -8, 20, 78, 45, -5, -3, 1,
        0, -8, 19, 77, 47, -5, -3, 1,
        0, -8, 18, 77, 48, -5, -3, 1,
        0, -8, 17, 77, 49, -5, -3, 1,
        0, -8, 16, 76, 50, -4, -3, 1,
        0, -8, 15, 76, 51, -4, -3, 1,
        0, -8, 15, 75, 52, -3, -4, 1,
        0, -7, 14, 74, 53, -3, -4, 1,
        0, -7, 13, 74, 54, -3, -4, 1,
        0, -7, 12, 73, 55, -2, -4, 1,
        0, -7, 11, 73, 56, -2, -4, 1,
        0, -7, 10, 72, 57, -1, -4, 1,
        1, -7, 10, 71, 58, -1, -5, 1,
        0, -7, 9, 71, 59, 0, -5, 1,
        1, -7, 8, 70, 60, 0, -5, 1,
        1, -7, 7, 69, 61, 1, -5, 1,
        1, -6, 6, 68, 62, 1, -5, 1,
        0, -6, 6, 68, 62, 2, -5, 1,
        1, -6, 5, 67, 63, 2, -5, 1,
        1, -6, 5, 66, 64, 3, -6, 1,
        1, -6, 4, 65, 65, 4, -6, 1,
        1, -6, 3, 64, 66, 5, -6, 1,
        1, -5, 2, 63, 67, 5, -6, 1,
        1, -5, 2, 62, 68, 6, -6, 0,
        1, -5, 1, 62, 68, 6, -6, 1,
        1, -5, 1, 61, 69, 7, -7, 1,
        1, -5, 0, 60, 70, 8, -7, 1,
        1, -5, 0, 59, 71, 9, -7, 0,
        1, -5, -1, 58, 71, 10, -7, 1,
        1, -4, -1, 57, 72, 10, -7, 0,
        1, -4, -2, 56, 73, 11, -7, 0,
        1, -4, -2, 55, 73, 12, -7, 0,
        1, -4, -3, 54, 74, 13, -7, 0,
        1, -4, -3, 53, 74, 14, -7, 0,
        1, -4, -3, 52, 75, 15, -8, 0,
        1, -3, -4, 51, 76, 15, -8, 0,
        1, -3, -4, 50, 76, 16, -8, 0,
        1, -3, -5, 49, 77, 17, -8, 0,
        1, -3, -5, 48, 77, 18, -8, 0,
        1, -3, -5, 47, 77, 19, -8, 0,
        1, -3, -5, 45, 78, 20, -8, 0,
        1, -2, -6, 44, 78, 21, -8, 0,
        1, -2, -6, 43, 78, 22, -8, 0,
        1, -2, -6, 42, 78, 23, -8, 0,
        1, -2, -7, 41, 79, 24, -8, 0,
        1, -2, -7, 40, 79, 25, -8, 0,
        1, -2, -7, 39, 79, 26, -8, 0,
        1, -2, -7, 38, 80, 27, -8, -1,
        1, -2, -7, 37, 80, 28, -8, -1,
        1, -2, -7, 36, 80, 29, -8, -1,
        1, -1, -8, 35, 80, 30, -8, -1,
        1, -1, -8, 34, 80, 31, -8, -1,
    };
    private static readonly short[] Filters750 =
    {
        2, -11, 25, 96, 25, -11, 2, 0,
        2, -11, 24, 96, 26, -11, 2, 0,
        2, -11, 22, 96, 28, -11, 2, 0,
        2, -10, 21, 96, 29, -12, 2, 0,
        2, -10, 19, 96, 31, -12, 2, 0,
        2, -10, 18, 95, 32, -11, 2, 0,
        2, -10, 17, 95, 34, -12, 2, 0,
        2, -9, 15, 95, 35, -12, 2, 0,
        2, -9, 14, 94, 37, -12, 2, 0,
        2, -9, 13, 94, 38, -12, 2, 0,
        2, -8, 12, 93, 40, -12, 1, 0,
        2, -8, 11, 93, 41, -12, 1, 0,
        2, -8, 9, 92, 43, -12, 1, 1,
        2, -8, 8, 92, 44, -12, 1, 1,
        2, -7, 7, 91, 46, -12, 1, 0,
        2, -7, 6, 90, 47, -12, 1, 1,
        2, -7, 5, 90, 49, -12, 1, 0,
        2, -6, 4, 89, 50, -12, 1, 0,
        2, -6, 3, 88, 52, -12, 0, 1,
        2, -6, 2, 87, 54, -12, 0, 1,
        2, -5, 1, 86, 55, -12, 0, 1,
        2, -5, 0, 85, 57, -12, 0, 1,
        2, -5, -1, 84, 58, -11, 0, 1,
        2, -5, -2, 83, 60, -11, 0, 1,
        2, -4, -2, 82, 61, -11, -1, 1,
        1, -4, -3, 81, 63, -10, -1, 1,
        2, -4, -4, 80, 64, -10, -1, 1,
        1, -4, -4, 79, 66, -10, -1, 1,
        1, -3, -5, 77, 67, -9, -1, 1,
        1, -3, -6, 76, 69, -9, -1, 1,
        1, -3, -6, 75, 70, -8, -2, 1,
        1, -2, -7, 74, 71, -8, -2, 1,
        1, -2, -7, 72, 72, -7, -2, 1,
        1, -2, -8, 71, 74, -7, -2, 1,
        1, -2, -8, 70, 75, -6, -3, 1,
        1, -1, -9, 69, 76, -6, -3, 1,
        1, -1, -9, 67, 77, -5, -3, 1,
        1, -1, -10, 66, 79, -4, -4, 1,
        1, -1, -10, 64, 80, -4, -4, 2,
        1, -1, -10, 63, 81, -3, -4, 1,
        1, -1, -11, 61, 82, -2, -4, 2,
        1, 0, -11, 60, 83, -2, -5, 2,
        1, 0, -11, 58, 84, -1, -5, 2,
        1, 0, -12, 57, 85, 0, -5, 2,
        1, 0, -12, 55, 86, 1, -5, 2,
        1, 0, -12, 54, 87, 2, -6, 2,
        1, 0, -12, 52, 88, 3, -6, 2,
        0, 1, -12, 50, 89, 4, -6, 2,
        0, 1, -12, 49, 90, 5, -7, 2,
        1, 1, -12, 47, 90, 6, -7, 2,
        0, 1, -12, 46, 91, 7, -7, 2,
        1, 1, -12, 44, 92, 8, -8, 2,
        1, 1, -12, 43, 92, 9, -8, 2,
        0, 1, -12, 41, 93, 11, -8, 2,
        0, 1, -12, 40, 93, 12, -8, 2,
        0, 2, -12, 38, 94, 13, -9, 2,
        0, 2, -12, 37, 94, 14, -9, 2,
        0, 2, -12, 35, 95, 15, -9, 2,
        0, 2, -12, 34, 95, 17, -10, 2,
        0, 2, -11, 32, 95, 18, -10, 2,
        0, 2, -12, 31, 96, 19, -10, 2,
        0, 2, -12, 29, 96, 21, -10, 2,
        0, 2, -11, 28, 96, 22, -11, 2,
        0, 2, -11, 26, 96, 24, -11, 2,
    };
    private static readonly short[] Filters875 =
    {
        3, -8, 13, 112, 13, -8, 3, 0,
        2, -7, 12, 112, 15, -8, 3, -1,
        3, -7, 10, 112, 17, -9, 3, -1,
        2, -6, 8, 112, 19, -9, 3, -1,
        2, -6, 7, 112, 21, -10, 3, -1,
        2, -5, 6, 111, 22, -10, 3, -1,
        2, -5, 4, 111, 24, -10, 3, -1,
        2, -4, 3, 110, 26, -11, 3, -1,
        2, -4, 1, 110, 28, -11, 3, -1,
        2, -4, 0, 109, 30, -12, 4, -1,
        1, -3, -1, 108, 32, -12, 4, -1,
        1, -3, -2, 108, 34, -13, 4, -1,
        1, -2, -4, 107, 36, -13, 4, -1,
        1, -2, -5, 106, 38, -13, 4, -1,
        1, -1, -6, 105, 40, -14, 4, -1,
        1, -1, -7, 104, 42, -14, 4, -1,
        1, -1, -7, 103, 44, -15, 4, -1,
        1, 0, -8, 101, 46, -15, 4, -1,
        1, 0, -9, 100, 48, -15, 4, -1,
        1, 0, -10, 99, 50, -15, 4, -1,
        1, 1, -11, 97, 53, -16, 4, -1,
        0, 1, -11, 96, 55, -16, 4, -1,
        0, 1, -12, 95, 57, -16, 4, -1,
        0, 2, -13, 93, 59, -16, 4, -1,
        0, 2, -13, 91, 61, -16, 4, -1,
        0, 2, -14, 90, 63, -16, 4, -1,
        0, 2, -14, 88, 65, -16, 4, -1,
        0, 2, -15, 86, 67, -16, 4, 0,
        0, 3, -15, 84, 69, -17, 4, 0,
        0, 3, -16, 83, 71, -17, 4, 0,
        0, 3, -16, 81, 73, -16, 3, 0,
        0, 3, -16, 79, 75, -16, 3, 0,
        0, 3, -16, 77, 77, -16, 3, 0,
        0, 3, -16, 75, 79, -16, 3, 0,
        0, 3, -16, 73, 81, -16, 3, 0,
        0, 4, -17, 71, 83, -16, 3, 0,
        0, 4, -17, 69, 84, -15, 3, 0,
        0, 4, -16, 67, 86, -15, 2, 0,
        -1, 4, -16, 65, 88, -14, 2, 0,
        -1, 4, -16, 63, 90, -14, 2, 0,
        -1, 4, -16, 61, 91, -13, 2, 0,
        -1, 4, -16, 59, 93, -13, 2, 0,
        -1, 4, -16, 57, 95, -12, 1, 0,
        -1, 4, -16, 55, 96, -11, 1, 0,
        -1, 4, -16, 53, 97, -11, 1, 1,
        -1, 4, -15, 50, 99, -10, 0, 1,
        -1, 4, -15, 48, 100, -9, 0, 1,
        -1, 4, -15, 46, 101, -8, 0, 1,
        -1, 4, -15, 44, 103, -7, -1, 1,
        -1, 4, -14, 42, 104, -7, -1, 1,
        -1, 4, -14, 40, 105, -6, -1, 1,
        -1, 4, -13, 38, 106, -5, -2, 1,
        -1, 4, -13, 36, 107, -4, -2, 1,
        -1, 4, -13, 34, 108, -2, -3, 1,
        -1, 4, -12, 32, 108, -1, -3, 1,
        -1, 4, -12, 30, 109, 0, -4, 2,
        -1, 3, -11, 28, 110, 1, -4, 2,
        -1, 3, -11, 26, 110, 3, -4, 2,
        -1, 3, -10, 24, 111, 4, -5, 2,
        -1, 3, -10, 22, 111, 6, -5, 2,
        -1, 3, -10, 21, 112, 7, -6, 2,
        -1, 3, -9, 19, 112, 8, -6, 2,
        -1, 3, -9, 17, 112, 10, -7, 3,
        -1, 3, -8, 15, 112, 12, -7, 2,
    };
    private static readonly short[] Filters1000 =
    {
        0, 0, 0, 128, 0, 0, 0, 0,
        0, 0, -1, 128, 2, -1, 0, 0,
        0, 1, -3, 127, 4, -2, 1, 0,
        0, 1, -4, 127, 6, -3, 1, 0,
        0, 2, -6, 126, 8, -3, 1, 0,
        0, 2, -7, 125, 11, -4, 1, 0,
        -1, 2, -8, 125, 13, -5, 2, 0,
        -1, 3, -9, 124, 15, -6, 2, 0,
        -1, 3, -10, 123, 18, -6, 2, -1,
        -1, 3, -11, 122, 20, -7, 3, -1,
        -1, 4, -12, 121, 22, -8, 3, -1,
        -1, 4, -13, 120, 25, -9, 3, -1,
        -1, 4, -14, 118, 28, -9, 3, -1,
        -1, 4, -15, 117, 30, -10, 4, -1,
        -1, 5, -16, 116, 32, -11, 4, -1,
        -1, 5, -16, 114, 35, -12, 4, -1,
        -1, 5, -17, 112, 38, -12, 4, -1,
        -1, 5, -18, 111, 40, -13, 5, -1,
        -1, 5, -18, 109, 43, -14, 5, -1,
        -1, 6, -19, 107, 45, -14, 5, -1,
        -1, 6, -19, 105, 48, -15, 5, -1,
        -1, 6, -19, 103, 51, -16, 5, -1,
        -1, 6, -20, 101, 53, -16, 6, -1,
        -1, 6, -20, 99, 56, -17, 6, -1,
        -1, 6, -20, 97, 58, -17, 6, -1,
        -1, 6, -20, 95, 61, -18, 6, -1,
        -2, 7, -20, 93, 64, -18, 6, -2,
        -2, 7, -20, 91, 66, -19, 6, -1,
        -2, 7, -20, 88, 69, -19, 6, -1,
        -2, 7, -20, 86, 71, -19, 6, -1,
        -2, 7, -20, 84, 74, -20, 7, -2,
        -2, 7, -20, 81, 76, -20, 7, -1,
        -2, 7, -20, 79, 79, -20, 7, -2,
        -1, 7, -20, 76, 81, -20, 7, -2,
        -2, 7, -20, 74, 84, -20, 7, -2,
        -1, 6, -19, 71, 86, -20, 7, -2,
        -1, 6, -19, 69, 88, -20, 7, -2,
        -1, 6, -19, 66, 91, -20, 7, -2,
        -2, 6, -18, 64, 93, -20, 7, -2,
        -1, 6, -18, 61, 95, -20, 6, -1,
        -1, 6, -17, 58, 97, -20, 6, -1,
        -1, 6, -17, 56, 99, -20, 6, -1,
        -1, 6, -16, 53, 101, -20, 6, -1,
        -1, 5, -16, 51, 103, -19, 6, -1,
        -1, 5, -15, 48, 105, -19, 6, -1,
        -1, 5, -14, 45, 107, -19, 6, -1,
        -1, 5, -14, 43, 109, -18, 5, -1,
        -1, 5, -13, 40, 111, -18, 5, -1,
        -1, 4, -12, 38, 112, -17, 5, -1,
        -1, 4, -12, 35, 114, -16, 5, -1,
        -1, 4, -11, 32, 116, -16, 5, -1,
        -1, 4, -10, 30, 117, -15, 4, -1,
        -1, 3, -9, 28, 118, -14, 4, -1,
        -1, 3, -9, 25, 120, -13, 4, -1,
        -1, 3, -8, 22, 121, -12, 4, -1,
        -1, 3, -7, 20, 122, -11, 3, -1,
        -1, 2, -6, 18, 123, -10, 3, -1,
        0, 2, -6, 15, 124, -9, 3, -1,
        0, 2, -5, 13, 125, -8, 2, -1,
        0, 1, -4, 11, 125, -7, 2, 0,
        0, 1, -3, 8, 126, -6, 2, 0,
        0, 1, -3, 6, 127, -4, 1, 0,
        0, 1, -2, 4, 127, -3, 1, 0,
        0, 0, -1, 2, 128, -1, 0, 0,
    };

    private static readonly short[] Down2SymevenHalf = { 56, 12, -3, -1 };
    private static readonly short[] Down2SymoddHalf = { 64, 35, 0, -3 };

    private static short[] ChooseInterpFilter(int inLength, int outLength)
    {
        int outLength16 = outLength * 16;
        if (outLength16 >= inLength * 16) return Filters1000;
        if (outLength16 >= inLength * 13) return Filters875;
        if (outLength16 >= inLength * 11) return Filters750;
        if (outLength16 >= inLength * 9) return Filters625;
        return Filters500;
    }

    private static void Interpolate(int[] input, int inOff, int inLength, int[] output, int outOff, int outLength, int maxVal)
    {
        var filters = ChooseInterpFilter(inLength, outLength);
        const int taps = 8;
        int delta = (int)((((uint)inLength << RS_SCALE_SUBPEL_BITS) + (uint)(outLength / 2)) / (uint)outLength);
        int offset = inLength > outLength
            ? (((inLength - outLength) << (RS_SCALE_SUBPEL_BITS - 1)) + outLength / 2) / outLength
            : -((((outLength - inLength) << (RS_SCALE_SUBPEL_BITS - 1)) + outLength / 2) / outLength);
        int y = offset + RS_SCALE_EXTRA_OFF;
        for (int x = 0; x < outLength; ++x, y += delta)
        {
            int intPel = y >> RS_SCALE_SUBPEL_BITS;
            int subPel = (y >> RS_SCALE_EXTRA_BITS) & RS_SUBPEL_MASK;
            int fo = subPel * taps;
            int sum = 0;
            for (int k = 0; k < taps; ++k)
            {
                int pk = Math.Clamp(intPel - taps / 2 + 1 + k, 0, inLength - 1);
                sum += filters[fo + k] * input[inOff + pk];
            }
            output[outOff + x] = Math.Clamp((sum + 64) >> 7, 0, maxVal);
        }
    }

    private static void Down2Symeven(int[] input, int inOff, int length, int[] output, int outOff, int maxVal)
    {
        var filter = Down2SymevenHalf;
        int o = outOff;
        for (int i = 0; i < length; i += 2)
        {
            int sum = 1 << 6;
            for (int j = 0; j < 4; ++j)
                sum += (input[inOff + Math.Max(i - j, 0)] + input[inOff + Math.Min(i + 1 + j, length - 1)]) * filter[j];
            sum >>= 7;
            output[o++] = Math.Clamp(sum, 0, maxVal);
        }
    }

    private static void Down2Symodd(int[] input, int inOff, int length, int[] output, int outOff, int maxVal)
    {
        var filter = Down2SymoddHalf;
        int o = outOff;
        for (int i = 0; i < length; i += 2)
        {
            int sum = (1 << 6) + input[inOff + i] * filter[0];
            for (int j = 1; j < 4; ++j)
                sum += (input[inOff + (i - j < 0 ? 0 : i - j)] + input[inOff + (i + j >= length ? length - 1 : i + j)]) * filter[j];
            sum >>= 7;
            output[o++] = Math.Clamp(sum, 0, maxVal);
        }
    }

    private static int GetDown2Length(int length, int steps)
    {
        for (int s = 0; s < steps; ++s) length = (length + 1) >> 1;
        return length;
    }

    private static int GetDown2Steps(int inLength, int outLength)
    {
        int steps = 0, proj;
        while ((proj = GetDown2Length(inLength, 1)) >= outLength)
        {
            ++steps;
            inLength = proj;
            if (inLength == 1) break;
        }
        return steps;
    }

    private static void ResizeMultistep(int[] input, int inOff, int length, int[] output, int outOff, int olength, int[] otmp, int maxVal)
    {
        if (length == olength) { Array.Copy(input, inOff, output, outOff, length); return; }
        int steps = GetDown2Steps(length, olength);
        if (steps > 0)
        {
            int[]? outB = null; int outO = 0;
            int filteredLength = length;
            int otmp2 = GetDown2Length(length, 1);
            for (int s = 0; s < steps; ++s)
            {
                int proj = GetDown2Length(filteredLength, 1);
                int[] inB = s == 0 ? input : outB!; int inO = s == 0 ? inOff : outO;
                if (s == steps - 1 && proj == olength) { outB = output; outO = outOff; }
                else { outB = otmp; outO = (s & 1) != 0 ? otmp2 : 0; }
                if ((filteredLength & 1) != 0) Down2Symodd(inB, inO, filteredLength, outB, outO, maxVal);
                else Down2Symeven(inB, inO, filteredLength, outB, outO, maxVal);
                filteredLength = proj;
            }
            if (filteredLength != olength) Interpolate(outB!, outO, filteredLength, output, outOff, olength, maxVal);
        }
        else Interpolate(input, inOff, length, output, outOff, olength, maxVal);
    }

    /// <summary>av1_resize_plane / highbd_resize_plane: rows, then columns, through resize_multistep.</summary>
    private static void ResizePlane(Func<int, int> get, int height, int width, int stride, Action<int, int> set, int height2, int width2,
        int outStride, int maxVal)
    {
        var intbuf = new int[width2 * height];
        var tmpbuf = new int[Math.Max(width, height)];
        var row = new int[width];
        var arrbuf = new int[height];
        var arrbuf2 = new int[height2];
        for (int i = 0; i < height; ++i)
        {
            for (int j = 0; j < width; j++) row[j] = get(stride * i + j);
            ResizeMultistep(row, 0, width, intbuf, width2 * i, width2, tmpbuf, maxVal);
        }
        for (int i = 0; i < width2; ++i)
        {
            for (int r = 0; r < height; r++) arrbuf[r] = intbuf[i + r * width2];
            ResizeMultistep(arrbuf, 0, height, arrbuf2, 0, height2, tmpbuf, maxVal);
            for (int r = 0; r < height2; r++) set(i + r * outStride, arrbuf2[r]);
        }
    }

    /// <summary>av1_resize_and_extend_frame_nonnormative.</summary>
    public static void ResizeAndExtendFrameNonnormative(AomFrameBuffer src, AomFrameBuffer dst)
    {
        int maxVal = (1 << src.BitDepth) - 1;
        for (int i = 0; i < Math.Min(src.NumPlanes, 3); ++i)
        {
            int isUv = i > 0 ? 1 : 0;
            int sOff = src.Offsets[i], dOff = dst.Offsets[i];
            if (src.Hbd)
            {
                var sb = src.Buffers16[i]; var db = dst.Buffers16[i];
                ResizePlane(p => sb[sOff + p], src.CropHeights[isUv], src.CropWidths[isUv], src.Strides[i], (p, v) => db[dOff + p] = (ushort)v,
                    dst.CropHeights[isUv], dst.CropWidths[isUv], dst.Strides[i], maxVal);
            }
            else
            {
                var sb = src.Buffers[i]; var db = dst.Buffers[i];
                ResizePlane(p => sb[sOff + p], src.CropHeights[isUv], src.CropWidths[isUv], src.Strides[i], (p, v) => db[dOff + p] = (byte)v,
                    dst.CropHeights[isUv], dst.CropWidths[isUv], dst.Strides[i], maxVal);
            }
        }
        ExtendFrameBorders(dst);
    }
}
