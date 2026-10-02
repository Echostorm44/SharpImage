using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// Port of libaom 3.14.1 av1/common/filter.h: the sub-pixel interpolation kernels (InterpKernel [SUBPEL_SHIFTS][8],
// flattened) and the InterpFilterParams lists, generated from filter.h.
internal static partial class AomFilter
{
    public const int SUBPEL_TAPS = 8, SUBPEL_BITS = 4, SUBPEL_MASK = 15, FILTER_BITS = 7;

    /// <summary>InterpFilterParams: the kernel table, its tap count (8 for every list entry) and the filter type.</summary>
    public readonly struct Params
    {
        public readonly short[] Filter;
        public readonly int Taps, Type;
        public Params(short[] f, int taps, int type) { Filter = f; Taps = taps; Type = type; }
        /// <summary>av1_get_interp_filter_subpel_kernel: the kernel offset in Filter for a subpel position.</summary>
        public int Kernel(int subpel) => Taps * subpel;
    }

    /// <summary>av1_interp_filter_params_list (EIGHTTAP_REGULAR, EIGHTTAP_SMOOTH, MULTITAP_SHARP, BILINEAR).</summary>
    public static readonly Params[] ParamsList;
    public static readonly Params[] Interp4Tap;

    static AomFilter()
    {
        ParamsList = new Params[]
        {
        new(SubPelFilters8, 8, EIGHTTAP_REGULAR), new(SubPelFilters8Smooth, 8, EIGHTTAP_SMOOTH),
        new(SubPelFilters8Sharp, 8, MULTITAP_SHARP), new(BilinearFilters, 8, BILINEAR), new(SubPelFilters12Sharp, 12, MULTITAP_SHARP2),
        };
        // av1_interp_4tap [SWITCHABLE_FILTERS + 1]
        Interp4Tap = new Params[]
        {
        new(SubPelFilters4, 8, EIGHTTAP_REGULAR), new(SubPelFilters4Smooth, 8, EIGHTTAP_SMOOTH),
        new(SubPelFilters4, 8, EIGHTTAP_REGULAR), new(BilinearFilters, 8, BILINEAR),
        };
    }

    /// <summary>av1_get_interp_filter_params_with_block_size.</summary>
    public static Params WithBlockSize(int interpFilter, int w) => w <= 4 && interpFilter != MULTITAP_SHARP2 ? Interp4Tap[interpFilter] : ParamsList[interpFilter];

    /// <summary>av1_sub_pel_filters_12sharp (MULTITAP_SHARP2: the temporal filter's predictor).</summary>
    internal static readonly short[] SubPelFilters12Sharp =
    {
        0, 0, 0, 0, 0, 128, 0, 0, 0, 0, 0, 0, 0, 1, -2, 3, -7, 127, 8, -4, 2, -1, 1, 0, -1, 2, -3, 6, -13, 124, 18, -8, 4, -2, 2, -1,
        -1, 3, -4, 8, -18, 120, 28, -12, 7, -4, 2, -1, -1, 3, -6, 10, -21, 115, 38, -15, 8, -5, 3, -1, -2, 4, -6, 12, -24, 108, 49, -18, 10, -6, 3, -2,
        -2, 4, -7, 13, -25, 100, 60, -21, 11, -7, 4, -2, -2, 4, -7, 13, -26, 91, 71, -24, 13, -7, 4, -2, -2, 4, -7, 13, -25, 81, 81, -25, 13, -7, 4, -2,
        -2, 4, -7, 13, -24, 71, 91, -26, 13, -7, 4, -2, -2, 4, -7, 11, -21, 60, 100, -25, 13, -7, 4, -2, -2, 3, -6, 10, -18, 49, 108, -24, 12, -6, 4, -2,
        -1, 3, -5, 8, -15, 38, 115, -21, 10, -6, 3, -1, -1, 2, -4, 7, -12, 28, 120, -18, 8, -4, 3, -1, -1, 2, -2, 4, -8, 18, 124, -13, 6, -3, 2, -1,
        0, 1, -1, 2, -4, 8, 127, -7, 3, -2, 1, 0,
    };

    /// <summary>av1_get_interp_filter_kernel.</summary>
    public static short[] InterpFilterKernel(int interpFilter, int subpelSearch)
        => subpelSearch == USE_2_TAPS ? Interp4Tap[BILINEAR].Filter
            : subpelSearch == USE_4_TAPS ? Interp4Tap[interpFilter].Filter : ParamsList[interpFilter].Filter;

    /// <summary>av1_get_filter.</summary>
    public static Params GetFilter(int subpelSearch) => subpelSearch switch
    {
        USE_2_TAPS => Interp4Tap[BILINEAR],
        USE_4_TAPS => Interp4Tap[EIGHTTAP_REGULAR],
        _ => ParamsList[EIGHTTAP_REGULAR],
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] BilinearFilters =
    {
        0, 0, 0, 128, 0, 0, 0, 0, 0, 0, 0, 120, 8, 0, 0, 0,
        0, 0, 0, 112, 16, 0, 0, 0, 0, 0, 0, 104, 24, 0, 0, 0,
        0, 0, 0, 96, 32, 0, 0, 0, 0, 0, 0, 88, 40, 0, 0, 0,
        0, 0, 0, 80, 48, 0, 0, 0, 0, 0, 0, 72, 56, 0, 0, 0,
        0, 0, 0, 64, 64, 0, 0, 0, 0, 0, 0, 56, 72, 0, 0, 0,
        0, 0, 0, 48, 80, 0, 0, 0, 0, 0, 0, 40, 88, 0, 0, 0,
        0, 0, 0, 32, 96, 0, 0, 0, 0, 0, 0, 24, 104, 0, 0, 0,
        0, 0, 0, 16, 112, 0, 0, 0, 0, 0, 0, 8, 120, 0, 0, 0,
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] SubPelFilters8 =
    {
        0, 0, 0, 128, 0, 0, 0, 0, 0, 2, -6, 126, 8, -2, 0, 0,
        0, 2, -10, 122, 18, -4, 0, 0, 0, 2, -12, 116, 28, -8, 2, 0,
        0, 2, -14, 110, 38, -10, 2, 0, 0, 2, -14, 102, 48, -12, 2, 0,
        0, 2, -16, 94, 58, -12, 2, 0, 0, 2, -14, 84, 66, -12, 2, 0,
        0, 2, -14, 76, 76, -14, 2, 0, 0, 2, -12, 66, 84, -14, 2, 0,
        0, 2, -12, 58, 94, -16, 2, 0, 0, 2, -12, 48, 102, -14, 2, 0,
        0, 2, -10, 38, 110, -14, 2, 0, 0, 2, -8, 28, 116, -12, 2, 0,
        0, 0, -4, 18, 122, -10, 2, 0, 0, 0, -2, 8, 126, -6, 2, 0,
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] SubPelFilters8Sharp =
    {
        0, 0, 0, 128, 0, 0, 0, 0, -2, 2, -6, 126, 8, -2, 2, 0,
        -2, 6, -12, 124, 16, -6, 4, -2, -2, 8, -18, 120, 26, -10, 6, -2,
        -4, 10, -22, 116, 38, -14, 6, -2, -4, 10, -22, 108, 48, -18, 8, -2,
        -4, 10, -24, 100, 60, -20, 8, -2, -4, 10, -24, 90, 70, -22, 10, -2,
        -4, 12, -24, 80, 80, -24, 12, -4, -2, 10, -22, 70, 90, -24, 10, -4,
        -2, 8, -20, 60, 100, -24, 10, -4, -2, 8, -18, 48, 108, -22, 10, -4,
        -2, 6, -14, 38, 116, -22, 10, -4, -2, 6, -10, 26, 120, -18, 8, -2,
        -2, 4, -6, 16, 124, -12, 6, -2, 0, 2, -2, 8, 126, -6, 2, -2,
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] SubPelFilters8Smooth =
    {
        0, 0, 0, 128, 0, 0, 0, 0, 0, 2, 28, 62, 34, 2, 0, 0,
        0, 0, 26, 62, 36, 4, 0, 0, 0, 0, 22, 62, 40, 4, 0, 0,
        0, 0, 20, 60, 42, 6, 0, 0, 0, 0, 18, 58, 44, 8, 0, 0,
        0, 0, 16, 56, 46, 10, 0, 0, 0, -2, 16, 54, 48, 12, 0, 0,
        0, -2, 14, 52, 52, 14, -2, 0, 0, 0, 12, 48, 54, 16, -2, 0,
        0, 0, 10, 46, 56, 16, 0, 0, 0, 0, 8, 44, 58, 18, 0, 0,
        0, 0, 6, 42, 60, 20, 0, 0, 0, 0, 4, 40, 62, 22, 0, 0,
        0, 0, 4, 36, 62, 26, 0, 0, 0, 0, 2, 34, 62, 28, 2, 0,
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] SubPelFilters4 =
    {
        0, 0, 0, 128, 0, 0, 0, 0, 0, 0, -4, 126, 8, -2, 0, 0,
        0, 0, -8, 122, 18, -4, 0, 0, 0, 0, -10, 116, 28, -6, 0, 0,
        0, 0, -12, 110, 38, -8, 0, 0, 0, 0, -12, 102, 48, -10, 0, 0,
        0, 0, -14, 94, 58, -10, 0, 0, 0, 0, -12, 84, 66, -10, 0, 0,
        0, 0, -12, 76, 76, -12, 0, 0, 0, 0, -10, 66, 84, -12, 0, 0,
        0, 0, -10, 58, 94, -14, 0, 0, 0, 0, -10, 48, 102, -12, 0, 0,
        0, 0, -8, 38, 110, -12, 0, 0, 0, 0, -6, 28, 116, -10, 0, 0,
        0, 0, -4, 18, 122, -8, 0, 0, 0, 0, -2, 8, 126, -4, 0, 0,
    };

    // [SUBPEL_SHIFTS] (128 values)
    internal static readonly short[] SubPelFilters4Smooth =
    {
        0, 0, 0, 128, 0, 0, 0, 0, 0, 0, 30, 62, 34, 2, 0, 0,
        0, 0, 26, 62, 36, 4, 0, 0, 0, 0, 22, 62, 40, 4, 0, 0,
        0, 0, 20, 60, 42, 6, 0, 0, 0, 0, 18, 58, 44, 8, 0, 0,
        0, 0, 16, 56, 46, 10, 0, 0, 0, 0, 14, 54, 48, 12, 0, 0,
        0, 0, 12, 52, 52, 12, 0, 0, 0, 0, 12, 48, 54, 14, 0, 0,
        0, 0, 10, 46, 56, 16, 0, 0, 0, 0, 8, 44, 58, 18, 0, 0,
        0, 0, 6, 42, 60, 20, 0, 0, 0, 0, 4, 40, 62, 22, 0, 0,
        0, 0, 4, 36, 62, 26, 0, 0, 0, 0, 2, 34, 62, 30, 0, 0,
    };
}
