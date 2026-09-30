using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1 av1/common/reconintra.c / reconintra.h / blockd.h: the constant tables of intra prediction.
internal static unsafe partial class AomReconIntra
{
    // enum { NEED_LEFT, NEED_ABOVE, NEED_ABOVERIGHT, NEED_ABOVELEFT, NEED_BOTTOMLEFT }
    private const int NEED_LEFT = 1 << 1, NEED_ABOVE = 1 << 2, NEED_ABOVERIGHT = 1 << 3, NEED_ABOVELEFT = 1 << 4,
        NEED_BOTTOMLEFT = 1 << 5;

    private const int INTRA_EDGE_FILT = 3, INTRA_EDGE_TAPS = 5, MAX_UPSAMPLE_SZ = 16;
    internal const int MAX_TX_SIZE = 64;
    internal const int NUM_INTRA_NEIGHBOUR_PIXELS = MAX_TX_SIZE * 2 + 32;
    internal const int FILTER_INTRA_SCALE_BITS = 4;
    internal const int ANGLE_STEP = 3, MAX_ANGLE_DELTA = 3;
    private const int MI_SIZE_LOG2 = 2, MAX_MIB_SIZE_LOG2 = 5;

    // PREDICTION_MODE values (enums.h) not in AomTables
    internal const int D45_PRED = 3, D135_PRED = 4, D113_PRED = 5, D157_PRED = 6, D203_PRED = 7, D67_PRED = 8,
        SMOOTH_V_PRED = 10, SMOOTH_H_PRED = 11, INTRA_MODES = 13;
    private const int INTRA_FRAME = 0;

    private static ReadOnlySpan<byte> ExtendModes => new byte[]
    {
        NEED_ABOVE | NEED_LEFT,                   // DC
        NEED_ABOVE,                               // V
        NEED_LEFT,                                // H
        NEED_ABOVE | NEED_ABOVERIGHT,             // D45
        NEED_LEFT | NEED_ABOVE | NEED_ABOVELEFT,  // D135
        NEED_LEFT | NEED_ABOVE | NEED_ABOVELEFT,  // D113
        NEED_LEFT | NEED_ABOVE | NEED_ABOVELEFT,  // D157
        NEED_LEFT | NEED_BOTTOMLEFT,              // D203
        NEED_ABOVE | NEED_ABOVERIGHT,             // D67
        NEED_LEFT | NEED_ABOVE,                   // SMOOTH
        NEED_LEFT | NEED_ABOVE,                   // SMOOTH_V
        NEED_LEFT | NEED_ABOVE,                   // SMOOTH_H
        NEED_LEFT | NEED_ABOVE | NEED_ABOVELEFT,  // PAETH
    };

    /// <summary>mode_to_angle_map (blockd.h).</summary>
    internal static ReadOnlySpan<byte> ModeToAngleMap => new byte[] { 0, 90, 180, 45, 135, 113, 157, 203, 67, 0, 0, 0, 0 };

    /// <summary>get_uv_mode's uv2y (blockd.h): UV_PREDICTION_MODE -> PREDICTION_MODE (UV_CFL_PRED -> DC_PRED).</summary>
    private static ReadOnlySpan<byte> Uv2y => new byte[]
    {
        DC_PRED, V_PRED, H_PRED, D45_PRED, D135_PRED, D113_PRED, D157_PRED, D203_PRED, D67_PRED, SMOOTH_PRED,
        SMOOTH_V_PRED, SMOOTH_H_PRED, PAETH_PRED, DC_PRED,
    };

    /// <summary>dr_intra_derivative (reconintra.h).</summary>
    internal static ReadOnlySpan<short> DrIntraDerivative => new short[]
    {
        0,    0, 0,
        1023, 0, 0,
        547,  0, 0,
        372,  0, 0, 0, 0,
        273,  0, 0,
        215,  0, 0,
        178,  0, 0,
        151,  0, 0,
        132,  0, 0,
        116,  0, 0,
        102,  0, 0, 0,
        90,   0, 0,
        80,   0, 0,
        71,   0, 0,
        64,   0, 0,
        57,   0, 0,
        51,   0, 0,
        45,   0, 0, 0,
        40,   0, 0,
        35,   0, 0,
        31,   0, 0,
        27,   0, 0,
        23,   0, 0,
        19,   0, 0,
        15,   0, 0, 0, 0,
        11,   0, 0,
        7,    0, 0,
        3,    0, 0,
    };

    /// <summary>av1_filter_intra_taps[FILTER_INTRA_MODES][8][8].</summary>
    internal static ReadOnlySpan<sbyte> FilterIntraTaps => new sbyte[]
    {
        -6, 10, 0, 0, 0, 12, 0, 0,
        -5, 2, 10, 0, 0, 9, 0, 0,
        -3, 1, 1, 10, 0, 7, 0, 0,
        -3, 1, 1, 2, 10, 5, 0, 0,
        -4, 6, 0, 0, 0, 2, 12, 0,
        -3, 2, 6, 0, 0, 2, 9, 0,
        -3, 2, 2, 6, 0, 2, 7, 0,
        -3, 1, 2, 2, 6, 3, 5, 0,

        -10, 16, 0, 0, 0, 10, 0, 0,
        -6, 0, 16, 0, 0, 6, 0, 0,
        -4, 0, 0, 16, 0, 4, 0, 0,
        -2, 0, 0, 0, 16, 2, 0, 0,
        -10, 16, 0, 0, 0, 0, 10, 0,
        -6, 0, 16, 0, 0, 0, 6, 0,
        -4, 0, 0, 16, 0, 0, 4, 0,
        -2, 0, 0, 0, 16, 0, 2, 0,

        -8, 8, 0, 0, 0, 16, 0, 0,
        -8, 0, 8, 0, 0, 16, 0, 0,
        -8, 0, 0, 8, 0, 16, 0, 0,
        -8, 0, 0, 0, 8, 16, 0, 0,
        -4, 4, 0, 0, 0, 0, 16, 0,
        -4, 0, 4, 0, 0, 0, 16, 0,
        -4, 0, 0, 4, 0, 0, 16, 0,
        -4, 0, 0, 0, 4, 0, 16, 0,

        -2, 8, 0, 0, 0, 10, 0, 0,
        -1, 3, 8, 0, 0, 6, 0, 0,
        -1, 2, 3, 8, 0, 4, 0, 0,
        0, 1, 2, 3, 8, 2, 0, 0,
        -1, 4, 0, 0, 0, 3, 10, 0,
        -1, 3, 4, 0, 0, 4, 6, 0,
        -1, 2, 3, 4, 0, 4, 4, 0,
        -1, 2, 2, 3, 4, 3, 3, 0,

        -12, 14, 0, 0, 0, 14, 0, 0,
        -10, 0, 14, 0, 0, 12, 0, 0,
        -9, 0, 0, 14, 0, 11, 0, 0,
        -8, 0, 0, 0, 14, 10, 0, 0,
        -10, 12, 0, 0, 0, 0, 14, 0,
        -9, 1, 12, 0, 0, 0, 12, 0,
        -8, 0, 0, 12, 0, 1, 11, 0,
        -7, 0, 0, 1, 12, 1, 9, 0,
    };

    // ---- has_tr tables -------------------------------------------------------------------------------------------
    private static readonly byte[] has_tr_4x4 =
    {
        255, 255, 255, 255, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        127, 127, 127, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        255, 127, 255, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        127, 127, 127, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        255, 255, 255, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        127, 127, 127, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        255, 127, 255, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
        127, 127, 127, 127, 85, 85, 85, 85, 119, 119, 119, 119, 85, 85, 85, 85,
    };
    private static readonly byte[] has_tr_4x8 =
    {
        255, 255, 255, 255, 119, 119, 119, 119, 127, 127, 127, 127, 119,
        119, 119, 119, 255, 127, 255, 127, 119, 119, 119, 119, 127, 127,
        127, 127, 119, 119, 119, 119, 255, 255, 255, 127, 119, 119, 119,
        119, 127, 127, 127, 127, 119, 119, 119, 119, 255, 127, 255, 127,
        119, 119, 119, 119, 127, 127, 127, 127, 119, 119, 119, 119,
    };
    private static readonly byte[] has_tr_8x4 =
    {
        255, 255, 0, 0, 85, 85, 0, 0, 119, 119, 0, 0, 85, 85, 0, 0,
        127, 127, 0, 0, 85, 85, 0, 0, 119, 119, 0, 0, 85, 85, 0, 0,
        255, 127, 0, 0, 85, 85, 0, 0, 119, 119, 0, 0, 85, 85, 0, 0,
        127, 127, 0, 0, 85, 85, 0, 0, 119, 119, 0, 0, 85, 85, 0, 0,
    };
    private static readonly byte[] has_tr_8x8 =
    {
        255, 255, 85, 85, 119, 119, 85, 85, 127, 127, 85, 85, 119, 119, 85, 85,
        255, 127, 85, 85, 119, 119, 85, 85, 127, 127, 85, 85, 119, 119, 85, 85,
    };
    private static readonly byte[] has_tr_8x16 =
    {
        255, 255, 119, 119, 127, 127, 119, 119,
        255, 127, 119, 119, 127, 127, 119, 119,
    };
    private static readonly byte[] has_tr_16x8 = { 255, 0, 85, 0, 119, 0, 85, 0, 127, 0, 85, 0, 119, 0, 85, 0 };
    private static readonly byte[] has_tr_16x16 = { 255, 85, 119, 85, 127, 85, 119, 85 };
    private static readonly byte[] has_tr_16x32 = { 255, 119, 127, 119 };
    private static readonly byte[] has_tr_32x16 = { 15, 5, 7, 5 };
    private static readonly byte[] has_tr_32x32 = { 95, 87 };
    private static readonly byte[] has_tr_32x64 = { 127 };
    private static readonly byte[] has_tr_64x32 = { 19 };
    private static readonly byte[] has_tr_64x64 = { 7 };
    private static readonly byte[] has_tr_64x128 = { 3 };
    private static readonly byte[] has_tr_128x64 = { 1 };
    private static readonly byte[] has_tr_128x128 = { 1 };
    private static readonly byte[] has_tr_4x16 =
    {
        255, 255, 255, 255, 127, 127, 127, 127, 255, 127, 255,
        127, 127, 127, 127, 127, 255, 255, 255, 127, 127, 127,
        127, 127, 255, 127, 255, 127, 127, 127, 127, 127,
    };
    private static readonly byte[] has_tr_16x4 =
    {
        255, 0, 0, 0, 85, 0, 0, 0, 119, 0, 0, 0, 85, 0, 0, 0,
        127, 0, 0, 0, 85, 0, 0, 0, 119, 0, 0, 0, 85, 0, 0, 0,
    };
    private static readonly byte[] has_tr_8x32 = { 255, 255, 127, 127, 255, 127, 127, 127 };
    private static readonly byte[] has_tr_32x8 = { 15, 0, 5, 0, 7, 0, 5, 0 };
    private static readonly byte[] has_tr_16x64 = { 255, 127 };
    private static readonly byte[] has_tr_64x16 = { 3, 1 };

    private static readonly byte[][] has_tr_tables =
    {
        has_tr_4x4,
        has_tr_4x8, has_tr_8x4, has_tr_8x8,
        has_tr_8x16, has_tr_16x8, has_tr_16x16,
        has_tr_16x32, has_tr_32x16, has_tr_32x32,
        has_tr_32x64, has_tr_64x32, has_tr_64x64,
        has_tr_64x128, has_tr_128x64, has_tr_128x128,
        has_tr_4x16, has_tr_16x4, has_tr_8x32,
        has_tr_32x8, has_tr_16x64, has_tr_64x16,
    };

    private static readonly byte[] has_tr_vert_8x8 =
    {
        255, 255, 0, 0, 119, 119, 0, 0, 127, 127, 0, 0, 119, 119, 0, 0,
        255, 127, 0, 0, 119, 119, 0, 0, 127, 127, 0, 0, 119, 119, 0, 0,
    };
    private static readonly byte[] has_tr_vert_16x16 = { 255, 0, 119, 0, 127, 0, 119, 0 };
    private static readonly byte[] has_tr_vert_32x32 = { 15, 7 };
    private static readonly byte[] has_tr_vert_64x64 = { 3 };

    private static readonly byte[]?[] has_tr_vert_tables =
    {
        null,
        has_tr_4x8, null, has_tr_vert_8x8,
        has_tr_8x16, null, has_tr_vert_16x16,
        has_tr_16x32, null, has_tr_vert_32x32,
        has_tr_32x64, null, has_tr_vert_64x64,
        has_tr_64x128, null, has_tr_128x128,
    };

    // ---- has_bl tables -------------------------------------------------------------------------------------------
    private static readonly byte[] has_bl_4x4 =
    {
        84, 85, 85, 85, 16, 17, 17, 17, 84, 85, 85, 85, 0,  1,  1,  1,  84, 85, 85,
        85, 16, 17, 17, 17, 84, 85, 85, 85, 0,  0,  1,  0,  84, 85, 85, 85, 16, 17,
        17, 17, 84, 85, 85, 85, 0,  1,  1,  1,  84, 85, 85, 85, 16, 17, 17, 17, 84,
        85, 85, 85, 0,  0,  0,  0,  84, 85, 85, 85, 16, 17, 17, 17, 84, 85, 85, 85,
        0,  1,  1,  1,  84, 85, 85, 85, 16, 17, 17, 17, 84, 85, 85, 85, 0,  0,  1,
        0,  84, 85, 85, 85, 16, 17, 17, 17, 84, 85, 85, 85, 0,  1,  1,  1,  84, 85,
        85, 85, 16, 17, 17, 17, 84, 85, 85, 85, 0,  0,  0,  0,
    };
    private static readonly byte[] has_bl_4x8 =
    {
        16, 17, 17, 17, 0, 1, 1, 1, 16, 17, 17, 17, 0, 0, 1, 0,
        16, 17, 17, 17, 0, 1, 1, 1, 16, 17, 17, 17, 0, 0, 0, 0,
        16, 17, 17, 17, 0, 1, 1, 1, 16, 17, 17, 17, 0, 0, 1, 0,
        16, 17, 17, 17, 0, 1, 1, 1, 16, 17, 17, 17, 0, 0, 0, 0,
    };
    private static readonly byte[] has_bl_8x4 =
    {
        254, 255, 84, 85, 254, 255, 16, 17, 254, 255, 84, 85, 254, 255, 0, 1,
        254, 255, 84, 85, 254, 255, 16, 17, 254, 255, 84, 85, 254, 255, 0, 0,
        254, 255, 84, 85, 254, 255, 16, 17, 254, 255, 84, 85, 254, 255, 0, 1,
        254, 255, 84, 85, 254, 255, 16, 17, 254, 255, 84, 85, 254, 255, 0, 0,
    };
    private static readonly byte[] has_bl_8x8 =
    {
        84, 85, 16, 17, 84, 85, 0, 1, 84, 85, 16, 17, 84, 85, 0, 0,
        84, 85, 16, 17, 84, 85, 0, 1, 84, 85, 16, 17, 84, 85, 0, 0,
    };
    private static readonly byte[] has_bl_8x16 = { 16, 17, 0, 1, 16, 17, 0, 0, 16, 17, 0, 1, 16, 17, 0, 0 };
    private static readonly byte[] has_bl_16x8 = { 254, 84, 254, 16, 254, 84, 254, 0, 254, 84, 254, 16, 254, 84, 254, 0 };
    private static readonly byte[] has_bl_16x16 = { 84, 16, 84, 0, 84, 16, 84, 0 };
    private static readonly byte[] has_bl_16x32 = { 16, 0, 16, 0 };
    private static readonly byte[] has_bl_32x16 = { 78, 14, 78, 14 };
    private static readonly byte[] has_bl_32x32 = { 4, 4 };
    private static readonly byte[] has_bl_32x64 = { 0 };
    private static readonly byte[] has_bl_64x32 = { 34 };
    private static readonly byte[] has_bl_64x64 = { 0 };
    private static readonly byte[] has_bl_64x128 = { 0 };
    private static readonly byte[] has_bl_128x64 = { 0 };
    private static readonly byte[] has_bl_128x128 = { 0 };
    private static readonly byte[] has_bl_4x16 =
    {
        0, 1, 1, 1, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0,
        0, 1, 1, 1, 0, 0, 1, 0, 0, 1, 1, 1, 0, 0, 0, 0,
    };
    private static readonly byte[] has_bl_16x4 =
    {
        254, 254, 254, 84, 254, 254, 254, 16, 254, 254, 254, 84, 254, 254, 254, 0,
        254, 254, 254, 84, 254, 254, 254, 16, 254, 254, 254, 84, 254, 254, 254, 0,
    };
    private static readonly byte[] has_bl_8x32 = { 0, 1, 0, 0, 0, 1, 0, 0 };
    private static readonly byte[] has_bl_32x8 = { 238, 78, 238, 14, 238, 78, 238, 14 };
    private static readonly byte[] has_bl_16x64 = { 0, 0 };
    private static readonly byte[] has_bl_64x16 = { 42, 42 };

    private static readonly byte[][] has_bl_tables =
    {
        has_bl_4x4,
        has_bl_4x8, has_bl_8x4, has_bl_8x8,
        has_bl_8x16, has_bl_16x8, has_bl_16x16,
        has_bl_16x32, has_bl_32x16, has_bl_32x32,
        has_bl_32x64, has_bl_64x32, has_bl_64x64,
        has_bl_64x128, has_bl_128x64, has_bl_128x128,
        has_bl_4x16, has_bl_16x4, has_bl_8x32,
        has_bl_32x8, has_bl_16x64, has_bl_64x16,
    };

    private static readonly byte[] has_bl_vert_8x8 =
    {
        254, 255, 16, 17, 254, 255, 0, 1, 254, 255, 16, 17, 254, 255, 0, 0,
        254, 255, 16, 17, 254, 255, 0, 1, 254, 255, 16, 17, 254, 255, 0, 0,
    };
    private static readonly byte[] has_bl_vert_16x16 = { 254, 16, 254, 0, 254, 16, 254, 0 };
    private static readonly byte[] has_bl_vert_32x32 = { 14, 14 };
    private static readonly byte[] has_bl_vert_64x64 = { 2 };

    private static readonly byte[]?[] has_bl_vert_tables =
    {
        null,
        has_bl_4x8, null, has_bl_vert_8x8,
        has_bl_8x16, null, has_bl_vert_16x16,
        has_bl_16x32, null, has_bl_vert_32x32,
        has_bl_32x64, null, has_bl_vert_64x64,
        has_bl_64x128, null, has_bl_128x128,
    };
}
