// AV1 decoder static lookup tables
// Ported from dav1d: src/tables.c, src/tables.h
// Reference: AV1 Bitstream & Decoding Process Specification v1.0.0

namespace SharpImage.Formats.Av1;

/// <summary>
/// Static lookup tables for the AV1 decoder.
/// All tables are direct ports from dav1d tables.c with identical
/// values and index semantics.
/// </summary>
public static partial class Av1Tables
{
    /// <summary>Number of switchable filters (Regular, Smooth, Sharp).</summary>
    public const byte NSwitchableFilters = 3;

    // ========================================================================
    // Partition Context
    // ========================================================================

    /// <summary>
    /// Above/left partition context values.
    /// Indexed by [above=0/left=1][blockLevel][partition].
    /// Maps to dav1d_al_part_ctx[2][N_BL_LEVELS][N_PARTITIONS].
    /// </summary>
    public static readonly byte[,,] AboveLeftPartCtx = new byte[2, 5, 10]
    {
        {
            // above (dim 0 = 0)
            //       none,   h,    v, split,  tts,  tbs,  tls,  trs,   h4,   v4
            { 0x00, 0x00, 0x10, 0xFF, 0x00, 0x10, 0x10, 0x10, 0xFF, 0xFF }, // bl128
            { 0x10, 0x10, 0x18, 0xFF, 0x10, 0x18, 0x18, 0x18, 0x10, 0x1C }, // bl64
            { 0x18, 0x18, 0x1C, 0xFF, 0x18, 0x1C, 0x1C, 0x1C, 0x18, 0x1E }, // bl32
            { 0x1C, 0x1C, 0x1E, 0xFF, 0x1C, 0x1E, 0x1E, 0x1E, 0x1C, 0x1F }, // bl16
            { 0x1E, 0x1E, 0x1F, 0x1F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, // bl8
        },
        {
            // left (dim 0 = 1)
            { 0x00, 0x10, 0x00, 0xFF, 0x10, 0x10, 0x00, 0x10, 0xFF, 0xFF }, // bl128
            { 0x10, 0x18, 0x10, 0xFF, 0x18, 0x18, 0x10, 0x18, 0x1C, 0x10 }, // bl64
            { 0x18, 0x1C, 0x18, 0xFF, 0x1C, 0x1C, 0x18, 0x1C, 0x1E, 0x18 }, // bl32
            { 0x1C, 0x1E, 0x1C, 0xFF, 0x1E, 0x1E, 0x1C, 0x1E, 0x1F, 0x1C }, // bl16
            { 0x1E, 0x1F, 0x1E, 0x1F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, // bl8
        }
    };

    // ========================================================================
    // Block Sizes per Partition
    // ========================================================================

    /// <summary>
    /// Block sizes resulting from each partition at each block level.
    /// Indexed by [blockLevel][partition][0=first/1=second].
    /// Values are Av1BlockSize enum ordinals.
    /// Maps to dav1d_block_sizes[N_BL_LEVELS][N_PARTITIONS][2].
    /// </summary>
    public static readonly byte[,,] BlockSizesPerPartition = new byte[5, 10, 2]
    {
        { // BL_128X128
            { (byte)Av1BlockSize.Bs128x128, 0 },                                      // NONE
            { (byte)Av1BlockSize.Bs128x64, 0 },                                       // H
            { (byte)Av1BlockSize.Bs64x128, 0 },                                       // V
            { 0, 0 },                                                                  // SPLIT (unused at 128)
            { (byte)Av1BlockSize.Bs64x64, (byte)Av1BlockSize.Bs128x64 },              // T_TOP_SPLIT
            { (byte)Av1BlockSize.Bs128x64, (byte)Av1BlockSize.Bs64x64 },              // T_BOTTOM_SPLIT
            { (byte)Av1BlockSize.Bs64x64, (byte)Av1BlockSize.Bs64x128 },              // T_LEFT_SPLIT
            { (byte)Av1BlockSize.Bs64x128, (byte)Av1BlockSize.Bs64x64 },              // T_RIGHT_SPLIT
            { 0, 0 },                                                                  // H4 (not at 128)
            { 0, 0 },                                                                  // V4 (not at 128)
        },
        { // BL_64X64
            { (byte)Av1BlockSize.Bs64x64, 0 },
            { (byte)Av1BlockSize.Bs64x32, 0 },
            { (byte)Av1BlockSize.Bs32x64, 0 },
            { 0, 0 },
            { (byte)Av1BlockSize.Bs32x32, (byte)Av1BlockSize.Bs64x32 },
            { (byte)Av1BlockSize.Bs64x32, (byte)Av1BlockSize.Bs32x32 },
            { (byte)Av1BlockSize.Bs32x32, (byte)Av1BlockSize.Bs32x64 },
            { (byte)Av1BlockSize.Bs32x64, (byte)Av1BlockSize.Bs32x32 },
            { (byte)Av1BlockSize.Bs64x16, 0 },
            { (byte)Av1BlockSize.Bs16x64, 0 },
        },
        { // BL_32X32
            { (byte)Av1BlockSize.Bs32x32, 0 },
            { (byte)Av1BlockSize.Bs32x16, 0 },
            { (byte)Av1BlockSize.Bs16x32, 0 },
            { 0, 0 },
            { (byte)Av1BlockSize.Bs16x16, (byte)Av1BlockSize.Bs32x16 },
            { (byte)Av1BlockSize.Bs32x16, (byte)Av1BlockSize.Bs16x16 },
            { (byte)Av1BlockSize.Bs16x16, (byte)Av1BlockSize.Bs16x32 },
            { (byte)Av1BlockSize.Bs16x32, (byte)Av1BlockSize.Bs16x16 },
            { (byte)Av1BlockSize.Bs32x8, 0 },
            { (byte)Av1BlockSize.Bs8x32, 0 },
        },
        { // BL_16X16
            { (byte)Av1BlockSize.Bs16x16, 0 },
            { (byte)Av1BlockSize.Bs16x8, 0 },
            { (byte)Av1BlockSize.Bs8x16, 0 },
            { 0, 0 },
            { (byte)Av1BlockSize.Bs8x8, (byte)Av1BlockSize.Bs16x8 },
            { (byte)Av1BlockSize.Bs16x8, (byte)Av1BlockSize.Bs8x8 },
            { (byte)Av1BlockSize.Bs8x8, (byte)Av1BlockSize.Bs8x16 },
            { (byte)Av1BlockSize.Bs8x16, (byte)Av1BlockSize.Bs8x8 },
            { (byte)Av1BlockSize.Bs16x4, 0 },
            { (byte)Av1BlockSize.Bs4x16, 0 },
        },
        { // BL_8X8
            { (byte)Av1BlockSize.Bs8x8, 0 },
            { (byte)Av1BlockSize.Bs8x4, 0 },
            { (byte)Av1BlockSize.Bs4x8, 0 },
            { (byte)Av1BlockSize.Bs4x4, 0 },
            { 0, 0 }, { 0, 0 }, { 0, 0 }, { 0, 0 }, { 0, 0 }, { 0, 0 },
        },
    };

    // ========================================================================
    // Block Dimensions
    // ========================================================================

    /// <summary>
    /// Block dimensions: {w4, h4, log2w, log2h} for each block size.
    /// w4/h4 are width/height in 4px blocks.
    /// Indexed by Av1BlockSize ordinal.
    /// Maps to dav1d_block_dimensions[N_BS_SIZES][4].
    /// </summary>
    public static readonly byte[,] BlockDimensions = new byte[22, 4]
    {
        { 32, 32, 5, 5 }, // BS_128x128
        { 32, 16, 5, 4 }, // BS_128x64
        { 16, 32, 4, 5 }, // BS_64x128
        { 16, 16, 4, 4 }, // BS_64x64
        { 16,  8, 4, 3 }, // BS_64x32
        { 16,  4, 4, 2 }, // BS_64x16
        {  8, 16, 3, 4 }, // BS_32x64
        {  8,  8, 3, 3 }, // BS_32x32
        {  8,  4, 3, 2 }, // BS_32x16
        {  8,  2, 3, 1 }, // BS_32x8
        {  4, 16, 2, 4 }, // BS_16x64
        {  4,  8, 2, 3 }, // BS_16x32
        {  4,  4, 2, 2 }, // BS_16x16
        {  4,  2, 2, 1 }, // BS_16x8
        {  4,  1, 2, 0 }, // BS_16x4
        {  2,  8, 1, 3 }, // BS_8x32
        {  2,  4, 1, 2 }, // BS_8x16
        {  2,  2, 1, 1 }, // BS_8x8
        {  2,  1, 1, 0 }, // BS_8x4
        {  1,  4, 0, 2 }, // BS_4x16
        {  1,  2, 0, 1 }, // BS_4x8
        {  1,  1, 0, 0 }, // BS_4x4
    };

    // ========================================================================
    // Transform Dimensions
    // ========================================================================

    /// <summary>
    /// Transform dimensions and metadata for each (rect) transform size.
    /// Indexed by transform size ordinal (0..18: TX_4X4..RTX_64X16).
    /// Maps to dav1d_txfm_dimensions[N_RECT_TX_SIZES].
    /// </summary>
    public static readonly Av1TxfmInfo[] TxfmDimensions = new Av1TxfmInfo[19]
    {
        new() { W =  1, H =  1, Lw = 0, Lh = 0, Min = 0, Max = 0, Sub = 0, Ctx = 0 }, // TX_4X4
        new() { W =  2, H =  2, Lw = 1, Lh = 1, Min = 1, Max = 1, Sub = 0, Ctx = 1 }, // TX_8X8 (sub=TX_4X4)
        new() { W =  4, H =  4, Lw = 2, Lh = 2, Min = 2, Max = 2, Sub = 1, Ctx = 2 }, // TX_16X16 (sub=TX_8X8)
        new() { W =  8, H =  8, Lw = 3, Lh = 3, Min = 3, Max = 3, Sub = 2, Ctx = 3 }, // TX_32X32 (sub=TX_16X16)
        new() { W = 16, H = 16, Lw = 4, Lh = 4, Min = 4, Max = 4, Sub = 3, Ctx = 4 }, // TX_64X64 (sub=TX_32X32)
        new() { W =  1, H =  2, Lw = 0, Lh = 1, Min = 0, Max = 1, Sub = 0, Ctx = 1 }, // RTX_4X8 (sub=TX_4X4)
        new() { W =  2, H =  1, Lw = 1, Lh = 0, Min = 0, Max = 1, Sub = 0, Ctx = 1 }, // RTX_8X4 (sub=TX_4X4)
        new() { W =  2, H =  4, Lw = 1, Lh = 2, Min = 1, Max = 2, Sub = 1, Ctx = 2 }, // RTX_8X16 (sub=TX_8X8)
        new() { W =  4, H =  2, Lw = 2, Lh = 1, Min = 1, Max = 2, Sub = 1, Ctx = 2 }, // RTX_16X8 (sub=TX_8X8)
        new() { W =  4, H =  8, Lw = 2, Lh = 3, Min = 2, Max = 3, Sub = 2, Ctx = 3 }, // RTX_16X32 (sub=TX_16X16)
        new() { W =  8, H =  4, Lw = 3, Lh = 2, Min = 2, Max = 3, Sub = 2, Ctx = 3 }, // RTX_32X16 (sub=TX_16X16)
        new() { W =  8, H = 16, Lw = 3, Lh = 4, Min = 3, Max = 4, Sub = 3, Ctx = 4 }, // RTX_32X64 (sub=TX_32X32)
        new() { W = 16, H =  8, Lw = 4, Lh = 3, Min = 3, Max = 4, Sub = 3, Ctx = 4 }, // RTX_64X32 (sub=TX_32X32)
        new() { W =  1, H =  4, Lw = 0, Lh = 2, Min = 0, Max = 2, Sub = 5, Ctx = 1 }, // RTX_4X16 (sub=RTX_4X8)
        new() { W =  4, H =  1, Lw = 2, Lh = 0, Min = 0, Max = 2, Sub = 6, Ctx = 1 }, // RTX_16X4 (sub=RTX_8X4)
        new() { W =  2, H =  8, Lw = 1, Lh = 3, Min = 1, Max = 3, Sub = 7, Ctx = 2 }, // RTX_8X32 (sub=RTX_8X16)
        new() { W =  8, H =  2, Lw = 3, Lh = 1, Min = 1, Max = 3, Sub = 8, Ctx = 2 }, // RTX_32X8 (sub=RTX_16X8)
        new() { W =  4, H = 16, Lw = 2, Lh = 4, Min = 2, Max = 4, Sub = 9, Ctx = 3 }, // RTX_16X64 (sub=RTX_16X32)
        new() { W = 16, H =  4, Lw = 4, Lh = 2, Min = 2, Max = 4, Sub = 10, Ctx = 3 }, // RTX_64X16 (sub=RTX_32X16)
    };

    // ========================================================================
    // Max Transform Size per Block Size
    // ========================================================================

    /// <summary>
    /// Maximum transform size for each block size and chroma format.
    /// Indexed by [Av1BlockSize][0=Y, 1=420, 2=422, 3=444].
    /// Values are (Rect)TxfmSize ordinals.
    /// Maps to dav1d_max_txfm_size_for_bs[N_BS_SIZES][4].
    /// </summary>
    public static readonly byte[,] MaxTxfmSizeForBlockSize = new byte[22, 4]
    {
        { 4, 3, 3, 3 },  // BS_128x128: TX_64X64, TX_32X32, TX_32X32, TX_32X32
        { 4, 3, 3, 3 },  // BS_128x64
        { 4, 3, 0, 3 },  // BS_64x128
        { 4, 3, 3, 3 },  // BS_64x64
        { 12, 10, 3, 3 }, // BS_64x32: RTX_64X32, RTX_32X16, TX_32X32, TX_32X32
        { 18, 16, 10, 10 }, // BS_64x16: RTX_64X16, RTX_32X8, RTX_32X16, RTX_32X16
        { 11, 9, 0, 3 },  // BS_32x64: RTX_32X64, RTX_16X32, 0, TX_32X32
        { 3, 2, 9, 3 },   // BS_32x32: TX_32X32, TX_16X16, RTX_16X32, TX_32X32
        { 10, 8, 2, 10 }, // BS_32x16: RTX_32X16, RTX_16X8, TX_16X16, RTX_32X16
        { 16, 14, 8, 16 }, // BS_32x8: RTX_32X8, RTX_16X4, RTX_16X8, RTX_32X8
        { 17, 15, 0, 9 }, // BS_16x64: RTX_16X64, RTX_8X32, 0, RTX_16X32
        { 9, 7, 0, 9 },   // BS_16x32: RTX_16X32, RTX_8X16, 0, RTX_16X32
        { 2, 1, 7, 2 },   // BS_16x16: TX_16X16, TX_8X8, RTX_8X16, TX_16X16
        { 8, 6, 1, 8 },  // BS_16x8: RTX_16X8, RTX_8X4, TX_8X8, RTX_16X8
        { 14, 6, 6, 14 }, // BS_16x4: RTX_16X4, RTX_8X4, RTX_8X4, RTX_16X4
        { 15, 13, 0, 15 }, // BS_8x32: RTX_8X32, RTX_4X16, 0, RTX_8X32
        { 7, 5, 0, 7 },   // BS_8x16: RTX_8X16, RTX_4X8, 0, RTX_8X16
        { 1, 0, 5, 1 },   // BS_8x8: TX_8X8, TX_4X4, RTX_4X8, TX_8X8
        { 6, 0, 0, 6 },   // BS_8x4: RTX_8X4, TX_4X4, TX_4X4, RTX_8X4
        { 13, 5, 0, 13 }, // BS_4x16: RTX_4X16, RTX_4X8, 0, RTX_4X16
        { 5, 0, 0, 5 },   // BS_4x8: RTX_4X8, TX_4X4, 0, RTX_4X8
        { 0, 0, 0, 0 },   // BS_4x4: TX_4X4 for all
    };

    // ========================================================================
    // Transform Type from UV Mode
    // ========================================================================

    /// <summary>
    /// Transform type from UV intra prediction mode.
    /// Indexed by Av1IntraPredMode (UV modes, 0..12 + CFL=13).
    /// Values are Av1TxType ordinals.
    /// Maps to dav1d_txtp_from_uvmode[N_UV_INTRA_PRED_MODES].
    /// </summary>
    public static readonly byte[] TxTypeFromUvMode = new byte[14]
    {
        (byte)Av1TxType.DctDct,      // DC_PRED
        (byte)Av1TxType.AdstDct,     // VERT_PRED
        (byte)Av1TxType.DctAdst,     // HOR_PRED
        (byte)Av1TxType.DctDct,      // DIAG_DOWN_LEFT
        (byte)Av1TxType.AdstAdst,    // DIAG_DOWN_RIGHT
        (byte)Av1TxType.AdstDct,     // VERT_RIGHT
        (byte)Av1TxType.DctAdst,     // HOR_DOWN
        (byte)Av1TxType.DctAdst,     // HOR_UP
        (byte)Av1TxType.AdstDct,     // VERT_LEFT
        (byte)Av1TxType.AdstAdst,    // SMOOTH
        (byte)Av1TxType.AdstDct,     // SMOOTH_V
        (byte)Av1TxType.DctAdst,     // SMOOTH_H
        (byte)Av1TxType.AdstAdst,    // PAETH
        0,                            // CFL (uses DCT_DCT in practice)
    };

    // ========================================================================
    // Compound Inter Prediction Mode Decomposition
    // ========================================================================

    /// <summary>
    /// Compound inter prediction mode decomposed into two single-ref modes.
    /// Indexed by [Av1CompInterPredMode][0=ref0/1=ref1].
    /// Values are Av1InterPredMode ordinals.
    /// Maps to dav1d_comp_inter_pred_modes[N_COMP_INTER_PRED_MODES][2].
    /// </summary>
    public static readonly byte[,] CompInterPredModes = new byte[8, 2]
    {
        { (byte)Av1InterPredMode.NearestMv, (byte)Av1InterPredMode.NearestMv }, // NEARESTMV_NEARESTMV
        { (byte)Av1InterPredMode.NearMv,    (byte)Av1InterPredMode.NearMv    }, // NEARMV_NEARMV
        { (byte)Av1InterPredMode.NearestMv, (byte)Av1InterPredMode.NewMv     }, // NEARESTMV_NEWMV
        { (byte)Av1InterPredMode.NewMv,     (byte)Av1InterPredMode.NearestMv }, // NEWMV_NEARESTMV
        { (byte)Av1InterPredMode.NearMv,    (byte)Av1InterPredMode.NewMv     }, // NEARMV_NEWMV
        { (byte)Av1InterPredMode.NewMv,     (byte)Av1InterPredMode.NearMv    }, // NEWMV_NEARMV
        { (byte)Av1InterPredMode.GlobalMv,  (byte)Av1InterPredMode.GlobalMv  }, // GLOBALMV_GLOBALMV
        { (byte)Av1InterPredMode.NewMv,     (byte)Av1InterPredMode.NewMv     }, // NEWMV_NEWMV
    };

    // ========================================================================
    // Partition Type Count
    // ========================================================================

    /// <summary>
    /// Number of valid partition types at each block level.
    /// Indexed by Av1BlockLevel.
    /// Maps to dav1d_partition_type_count[N_BL_LEVELS].
    /// </summary>
    public static readonly byte[] PartitionTypeCount = new byte[5]
    {
        7,  // BL_128X128: N_PARTITIONS - 3 (no H4, V4, or SPLIT)
        9,  // BL_64X64:   N_PARTITIONS - 1
        9,  // BL_32X32:   N_PARTITIONS - 1
        9,  // BL_16X16:   N_PARTITIONS - 1
        3,  // BL_8X8:     N_SUB8X8_PARTITIONS - 1
    };

    // ========================================================================
    // Transform Types per Set
    // ========================================================================

    /// <summary>
    /// Transform types grouped by set (Intra2, Intra1, Inter2, Inter1).
    /// Set offsets: Intra2=0(5), Intra1=5(7), Inter2=12(12), Inter1=24(16).
    /// Values are Av1TxType ordinals.
    /// Maps to dav1d_tx_types_per_set[40].
    /// </summary>
    public static readonly byte[] TxTypesPerSet = new byte[40]
    {
        // Intra2 (5 types)
        (byte)Av1TxType.Identity, (byte)Av1TxType.DctDct, (byte)Av1TxType.AdstAdst,
        (byte)Av1TxType.AdstDct, (byte)Av1TxType.DctAdst,
        // Intra1 (7 types)
        (byte)Av1TxType.Identity, (byte)Av1TxType.DctDct, (byte)Av1TxType.VDct,
        (byte)Av1TxType.HDct, (byte)Av1TxType.AdstAdst, (byte)Av1TxType.AdstDct,
        (byte)Av1TxType.DctAdst,
        // Inter2 (12 types)
        (byte)Av1TxType.Identity, (byte)Av1TxType.VDct, (byte)Av1TxType.HDct,
        (byte)Av1TxType.DctDct, (byte)Av1TxType.AdstDct, (byte)Av1TxType.DctAdst,
        (byte)Av1TxType.FlipAdstDct, (byte)Av1TxType.DctFlipAdst,
        (byte)Av1TxType.AdstAdst, (byte)Av1TxType.FlipAdstFlipAdst,
        (byte)Av1TxType.AdstFlipAdst, (byte)Av1TxType.FlipAdstAdst,
        // Inter1 (16 types)
        (byte)Av1TxType.Identity, (byte)Av1TxType.VDct, (byte)Av1TxType.HDct,
        (byte)Av1TxType.VAdst, (byte)Av1TxType.HAdst, (byte)Av1TxType.VFlipAdst,
        (byte)Av1TxType.HFlipAdst, (byte)Av1TxType.DctDct, (byte)Av1TxType.AdstDct,
        (byte)Av1TxType.DctAdst, (byte)Av1TxType.FlipAdstDct, (byte)Av1TxType.DctFlipAdst,
        (byte)Av1TxType.AdstAdst, (byte)Av1TxType.FlipAdstFlipAdst,
        (byte)Av1TxType.AdstFlipAdst, (byte)Av1TxType.FlipAdstAdst,
    };

    // ========================================================================
    // Y-Mode Size Context
    // ========================================================================

    /// <summary>
    /// Y-mode CDF context index by block size.
    /// Indexed by Av1BlockSize ordinal.
    /// Maps to dav1d_ymode_size_context[N_BS_SIZES].
    /// </summary>
    public static readonly byte[] YModeSizeContext = new byte[22]
    {
        3, 3, 3, 3, 3, 2, 3, 3, 2, 1, // 128x128..32x8
        2, 2, 2, 1, 0, 1, 1, 1, 0, 0, // 16x64..4x16
        0, 0,                           // 4x8, 4x4
    };

    // ========================================================================
    // Level-of-Detail Context Offsets
    // ========================================================================

    /// <summary>
    /// Coefficient level context offsets.
    /// Indexed by [aspect: 0=square, 1=wide, 2=tall][row][col].
    /// Maps to dav1d_lo_ctx_offsets[3][5][5].
    /// </summary>
    public static readonly byte[,,] LoCtxOffsets = new byte[3, 5, 5]
    {
        { // w == h (square)
            {  0,  1,  6,  6, 21 },
            {  1,  6,  6, 21, 21 },
            {  6,  6, 21, 21, 21 },
            {  6, 21, 21, 21, 21 },
            { 21, 21, 21, 21, 21 },
        },
        { // w > h (wide)
            {  0, 16,  6,  6, 21 },
            { 16, 16,  6, 21, 21 },
            { 16, 16, 21, 21, 21 },
            { 16, 16, 21, 21, 21 },
            { 16, 16, 21, 21, 21 },
        },
        { // w < h (tall)
            {  0, 11, 11, 11, 11 },
            { 11, 11, 11, 11, 11 },
            {  6,  6, 21, 21, 21 },
            {  6, 21, 21, 21, 21 },
            { 21, 21, 21, 21, 21 },
        },
    };

    // ========================================================================
    // Skip Context
    // ========================================================================

    /// <summary>
    /// Context for skip signaling based on above/left coded coefficients.
    /// Indexed by [above_ctx][left_ctx].
    /// Maps to dav1d_skip_ctx[5][5].
    /// </summary>
    public static readonly byte[,] SkipCtx = new byte[5, 5]
    {
        { 1, 2, 2, 2, 3 },
        { 2, 4, 4, 4, 5 },
        { 2, 4, 4, 4, 5 },
        { 2, 4, 4, 4, 5 },
        { 3, 5, 5, 5, 6 },
    };

    // ========================================================================
    // Transform Type Class
    // ========================================================================

    /// <summary>
    /// Transform class (2D/V/H) for each transform type.
    /// Indexed by Av1TxType (including WHT_WHT at index 16).
    /// Values are Av1TxClass ordinals.
    /// Maps to dav1d_tx_type_class[N_TX_TYPES_PLUS_LL].
    /// </summary>
    public static readonly byte[] TxTypeClass = new byte[17]
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,   // DCT_DCT..IDTX: all TX_CLASS_2D
        2, 1, 2, 1, 2, 1,                 // V_DCT, H_DCT, V_ADST, H_ADST, V_FLIPADST, H_FLIPADST
        0,                                 // WHT_WHT: TX_CLASS_2D
    };

    // ========================================================================
    // Filter 2D Lookup
    // ========================================================================

    /// <summary>
    /// 2D interpolation filter from horizontal × vertical 1D filters.
    /// Indexed by [horizontal Av1FilterMode][vertical Av1FilterMode].
    /// Values are Av1Filter2d ordinals.
    /// Maps to dav1d_filter_2d[DAV1D_N_FILTERS][DAV1D_N_FILTERS].
    /// </summary>
    public static readonly byte[,] Filter2d = new byte[4, 4]
    {
        //                Regular  Smooth  Sharp  Bilinear
        /* Regular  */ {    0,      1,      2,     0 },
        /* Smooth   */ {    6,      7,      8,     0 },
        /* Sharp    */ {    3,      4,      5,     0 },
        /* Bilinear */ {    0,      0,      0,     9 },
    };

    // ========================================================================
    // Filter Direction Decomposition
    // ========================================================================

    /// <summary>
    /// Decompose a 2D filter into its horizontal and vertical 1D components.
    /// Indexed by [Av1Filter2d][0=vertical, 1=horizontal].
    /// Values are Av1FilterMode ordinals.
    /// Maps to dav1d_filter_dir[N_2D_FILTERS][2].
    /// </summary>
    public static readonly byte[,] FilterDir = new byte[10, 2]
    {
        { 0, 0 }, // 8TAP_REGULAR:        Regular, Regular
        { 1, 0 }, // 8TAP_REGULAR_SMOOTH: Smooth,  Regular
        { 2, 0 }, // 8TAP_REGULAR_SHARP:  Sharp,   Regular
        { 0, 2 }, // 8TAP_SHARP_REGULAR:  Regular, Sharp
        { 1, 2 }, // 8TAP_SHARP_SMOOTH:   Smooth,  Sharp
        { 2, 2 }, // 8TAP_SHARP:          Sharp,   Sharp
        { 0, 1 }, // 8TAP_SMOOTH_REGULAR: Regular, Smooth
        { 1, 1 }, // 8TAP_SMOOTH:         Smooth,  Smooth
        { 2, 1 }, // 8TAP_SMOOTH_SHARP:   Sharp,   Smooth
        { 3, 3 }, // BILINEAR:            Bilinear, Bilinear
    };

    // ========================================================================
    // Filter Mode to Y Intra Mode
    // ========================================================================

    /// <summary>
    /// Maps filter mode context to Y intra mode for filter_intra.
    /// Maps to dav1d_filter_mode_to_y_mode[5].
    /// </summary>
    public static readonly byte[] FilterModeToYMode = new byte[5]
    {
        (byte)Av1IntraPredMode.Dc,
        (byte)Av1IntraPredMode.Vertical,
        (byte)Av1IntraPredMode.Horizontal,
        (byte)Av1IntraPredMode.HorizontalDown,
        (byte)Av1IntraPredMode.Dc,
    };

    // ========================================================================
    // Intra Mode Context
    // ========================================================================

    /// <summary>
    /// Context index for intra mode signaling.
    /// Indexed by Av1IntraPredMode (0..12).
    /// Maps to dav1d_intra_mode_context[N_INTRA_PRED_MODES].
    /// </summary>
    public static readonly byte[] IntraModeContext = new byte[13]
    {
        0, 1, 2, 3, 4, 4, 4, 4, 3, 0, 1, 2, 0,
    };

    // ========================================================================
    // Wedge Context Lookup
    // ========================================================================

    /// <summary>
    /// Wedge sign context lookup by block size.
    /// Only defined for block sizes that support wedge compound.
    /// Indexed by Av1BlockSize ordinal (undefined entries are 0).
    /// Maps to dav1d_wedge_ctx_lut[N_BS_SIZES].
    /// </summary>
    public static readonly byte[] WedgeCtxLut = new byte[22]
    {
        0, 0, 0, 0, 0, 0, 0, 6, 5, 8,  // 128x128..32x8
        0, 4, 3, 2, 0, 7, 1, 0, 0, 0,  // 16x64..4x16
        0, 0,                            // 4x8, 4x4
    };

    // ========================================================================
    // Default Warped Motion Parameters
    // ========================================================================

    /// <summary>
    /// Default (identity) warped motion parameters.
    /// Maps to dav1d_default_wm_params.
    /// </summary>
    public static readonly Av1WarpedMotionParams DefaultWarpedMotionParams = new()
    {
        Type = Av1WarpedMotionType.Identity,
        Matrix2 = 1 << 16,
        Matrix5 = 1 << 16,
    };

    // ========================================================================
    // CDEF Directions
    // ========================================================================

    /// <summary>
    /// CDEF filter direction offsets (12 entries: 2 wrap + 8 + 2 wrap).
    /// Each entry is [pass0_offset, pass1_offset] as signed stride offsets.
    /// Stride is 12 (CDEF block width + 2*border).
    /// Maps to dav1d_cdef_directions[12][2].
    /// </summary>
    public static readonly sbyte[,] CdefDirections = new sbyte[12, 2]
    {
        {  1 * 12 + 0,  2 * 12 + 0 }, // 6 (wrap)
        {  1 * 12 + 0,  2 * 12 - 1 }, // 7 (wrap)
        { -1 * 12 + 1, -2 * 12 + 2 }, // 0
        {  0 * 12 + 1, -1 * 12 + 2 }, // 1
        {  0 * 12 + 1,  0 * 12 + 2 }, // 2
        {  0 * 12 + 1,  1 * 12 + 2 }, // 3
        {  1 * 12 + 1,  2 * 12 + 2 }, // 4
        {  1 * 12 + 0,  2 * 12 + 1 }, // 5
        {  1 * 12 + 0,  2 * 12 + 0 }, // 6
        {  1 * 12 + 0,  2 * 12 - 1 }, // 7
        { -1 * 12 + 1, -2 * 12 + 2 }, // 0 (wrap)
        {  0 * 12 + 1, -1 * 12 + 2 }, // 1 (wrap)
    };

    // ========================================================================
    // Self-Guided Restoration Parameters
    // ========================================================================

    /// <summary>
    /// Self-guided restoration filter parameters.
    /// Indexed by [sgr_set_index][0=s0, 1=s1].
    /// Maps to dav1d_sgr_params[16][2].
    /// </summary>
    public static readonly ushort[,] SgrParams = new ushort[16, 2]
    {
        { 140, 3236 }, { 112, 2158 }, {  93, 1618 }, {  80, 1438 },
        {  70, 1295 }, {  58, 1177 }, {  47, 1079 }, {  37,  996 },
        {  30,  925 }, {  25,  863 }, {   0, 2589 }, {   0, 1618 },
        {   0, 1177 }, {   0,  925 }, {  56,    0 }, {  22,    0 },
    };

    /// <summary>
    /// Self-guided restoration x-by-x lookup table.
    /// Maps to dav1d_sgr_x_by_x[256].
    /// </summary>
    public static readonly byte[] SgrXByX = new byte[256]
    {
        255, 128,  85,  64,  51,  43,  37,  32,  28,  26,  23,  21,  20,  18,  17,
         16,  15,  14,  13,  13,  12,  12,  11,  11,  10,  10,   9,   9,   9,   9,
          8,   8,   8,   8,   7,   7,   7,   7,   7,   6,   6,   6,   6,   6,   6,
          6,   5,   5,   5,   5,   5,   5,   5,   5,   5,   5,   4,   4,   4,   4,
          4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   4,   3,   3,
          3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,
          3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   3,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,   2,
          2,   2,   2,   2,   2,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,   1,
          0,
    };

    // ========================================================================
    // Subpel Interpolation Filters
    // ========================================================================

    /// <summary>
    /// 8-tap subpel interpolation filter coefficients.
    /// Indexed by [filter_type][subpel_position 0..14][tap 0..7].
    /// filter_type: 0=Regular, 1=Smooth, 2=Sharp, 3=Regular(w<=4), 4=Smooth(w<=4), 5=BilinScaled.
    /// Maps to dav1d_mc_subpel_filters[6][15][8].
    /// </summary>
    public static readonly sbyte[,,] McSubpelFilters = new sbyte[6, 15, 8]
    {
        { // 0: 8TAP_REGULAR
            {   0,   1,  -3,  63,   4,  -1,   0,   0 },
            {   0,   1,  -5,  61,   9,  -2,   0,   0 },
            {   0,   1,  -6,  58,  14,  -4,   1,   0 },
            {   0,   1,  -7,  55,  19,  -5,   1,   0 },
            {   0,   1,  -7,  51,  24,  -6,   1,   0 },
            {   0,   1,  -8,  47,  29,  -6,   1,   0 },
            {   0,   1,  -7,  42,  33,  -6,   1,   0 },
            {   0,   1,  -7,  38,  38,  -7,   1,   0 },
            {   0,   1,  -6,  33,  42,  -7,   1,   0 },
            {   0,   1,  -6,  29,  47,  -8,   1,   0 },
            {   0,   1,  -6,  24,  51,  -7,   1,   0 },
            {   0,   1,  -5,  19,  55,  -7,   1,   0 },
            {   0,   1,  -4,  14,  58,  -6,   1,   0 },
            {   0,   0,  -2,   9,  61,  -5,   1,   0 },
            {   0,   0,  -1,   4,  63,  -3,   1,   0 },
        },
        { // 1: 8TAP_SMOOTH
            {   0,   1,  14,  31,  17,   1,   0,   0 },
            {   0,   0,  13,  31,  18,   2,   0,   0 },
            {   0,   0,  11,  31,  20,   2,   0,   0 },
            {   0,   0,  10,  30,  21,   3,   0,   0 },
            {   0,   0,   9,  29,  22,   4,   0,   0 },
            {   0,   0,   8,  28,  23,   5,   0,   0 },
            {   0,  -1,   8,  27,  24,   6,   0,   0 },
            {   0,  -1,   7,  26,  26,   7,  -1,   0 },
            {   0,   0,   6,  24,  27,   8,  -1,   0 },
            {   0,   0,   5,  23,  28,   8,   0,   0 },
            {   0,   0,   4,  22,  29,   9,   0,   0 },
            {   0,   0,   3,  21,  30,  10,   0,   0 },
            {   0,   0,   2,  20,  31,  11,   0,   0 },
            {   0,   0,   2,  18,  31,  13,   0,   0 },
            {   0,   0,   1,  17,  31,  14,   1,   0 },
        },
        { // 2: 8TAP_SHARP
            {  -1,   1,  -3,  63,   4,  -1,   1,   0 },
            {  -1,   3,  -6,  62,   8,  -3,   2,  -1 },
            {  -1,   4,  -9,  60,  13,  -5,   3,  -1 },
            {  -2,   5, -11,  58,  19,  -7,   3,  -1 },
            {  -2,   5, -11,  54,  24,  -9,   4,  -1 },
            {  -2,   5, -12,  50,  30, -10,   4,  -1 },
            {  -2,   5, -12,  45,  35, -11,   5,  -1 },
            {  -2,   6, -12,  40,  40, -12,   6,  -2 },
            {  -1,   5, -11,  35,  45, -12,   5,  -2 },
            {  -1,   4, -10,  30,  50, -12,   5,  -2 },
            {  -1,   4,  -9,  24,  54, -11,   5,  -2 },
            {  -1,   3,  -7,  19,  58, -11,   5,  -2 },
            {  -1,   3,  -5,  13,  60,  -9,   4,  -1 },
            {  -1,   2,  -3,   8,  62,  -6,   3,  -1 },
            {   0,   1,  -1,   4,  63,  -3,   1,  -1 },
        },
        { // 3: 8TAP_REGULAR (width <= 4)
            {   0,   0,  -2,  63,   4,  -1,   0,   0 },
            {   0,   0,  -4,  61,   9,  -2,   0,   0 },
            {   0,   0,  -5,  58,  14,  -3,   0,   0 },
            {   0,   0,  -6,  55,  19,  -4,   0,   0 },
            {   0,   0,  -6,  51,  24,  -5,   0,   0 },
            {   0,   0,  -7,  47,  29,  -5,   0,   0 },
            {   0,   0,  -6,  42,  33,  -5,   0,   0 },
            {   0,   0,  -6,  38,  38,  -6,   0,   0 },
            {   0,   0,  -5,  33,  42,  -6,   0,   0 },
            {   0,   0,  -5,  29,  47,  -7,   0,   0 },
            {   0,   0,  -5,  24,  51,  -6,   0,   0 },
            {   0,   0,  -4,  19,  55,  -6,   0,   0 },
            {   0,   0,  -3,  14,  58,  -5,   0,   0 },
            {   0,   0,  -2,   9,  61,  -4,   0,   0 },
            {   0,   0,  -1,   4,  63,  -2,   0,   0 },
        },
        { // 4: 8TAP_SMOOTH (width <= 4)
            {   0,   0,  15,  31,  17,   1,   0,   0 },
            {   0,   0,  13,  31,  18,   2,   0,   0 },
            {   0,   0,  11,  31,  20,   2,   0,   0 },
            {   0,   0,  10,  30,  21,   3,   0,   0 },
            {   0,   0,   9,  29,  22,   4,   0,   0 },
            {   0,   0,   8,  28,  23,   5,   0,   0 },
            {   0,   0,   7,  27,  24,   6,   0,   0 },
            {   0,   0,   6,  26,  26,   6,   0,   0 },
            {   0,   0,   6,  24,  27,   7,   0,   0 },
            {   0,   0,   5,  23,  28,   8,   0,   0 },
            {   0,   0,   4,  22,  29,   9,   0,   0 },
            {   0,   0,   3,  21,  30,  10,   0,   0 },
            {   0,   0,   2,  20,  31,  11,   0,   0 },
            {   0,   0,   2,  18,  31,  13,   0,   0 },
            {   0,   0,   1,  17,  31,  15,   0,   0 },
        },
        { // 5: Bilinear (scaled)
            {   0,   0,   0,  60,   4,   0,   0,   0 },
            {   0,   0,   0,  56,   8,   0,   0,   0 },
            {   0,   0,   0,  52,  12,   0,   0,   0 },
            {   0,   0,   0,  48,  16,   0,   0,   0 },
            {   0,   0,   0,  44,  20,   0,   0,   0 },
            {   0,   0,   0,  40,  24,   0,   0,   0 },
            {   0,   0,   0,  36,  28,   0,   0,   0 },
            {   0,   0,   0,  32,  32,   0,   0,   0 },
            {   0,   0,   0,  28,  36,   0,   0,   0 },
            {   0,   0,   0,  24,  40,   0,   0,   0 },
            {   0,   0,   0,  20,  44,   0,   0,   0 },
            {   0,   0,   0,  16,  48,   0,   0,   0 },
            {   0,   0,   0,  12,  52,   0,   0,   0 },
            {   0,   0,   0,   8,  56,   0,   0,   0 },
            {   0,   0,   0,   4,  60,   0,   0,   0 },
        },
    };

    // ========================================================================
    // Warp Filter
    // ========================================================================

    /// <summary>
    /// Warp motion compensation filter (193 positions × 8 taps).
    /// Maps to dav1d_mc_warp_filter[193][8].
    /// </summary>
    public static readonly sbyte[,] McWarpFilter = new sbyte[193, 8]
    {
        // [-1, 0) — 64 entries
        { 0,   0, 127,   1,   0, 0, 0, 0 }, { 0,  -1, 127,   2,   0, 0, 0, 0 },
        { 1,  -3, 127,   4,  -1, 0, 0, 0 }, { 1,  -4, 126,   6,  -2, 1, 0, 0 },
        { 1,  -5, 126,   8,  -3, 1, 0, 0 }, { 1,  -6, 125,  11,  -4, 1, 0, 0 },
        { 1,  -7, 124,  13,  -4, 1, 0, 0 }, { 2,  -8, 123,  15,  -5, 1, 0, 0 },
        { 2,  -9, 122,  18,  -6, 1, 0, 0 }, { 2, -10, 121,  20,  -6, 1, 0, 0 },
        { 2, -11, 120,  22,  -7, 2, 0, 0 }, { 2, -12, 119,  25,  -8, 2, 0, 0 },
        { 3, -13, 117,  27,  -8, 2, 0, 0 }, { 3, -13, 116,  29,  -9, 2, 0, 0 },
        { 3, -14, 114,  32, -10, 3, 0, 0 }, { 3, -15, 113,  35, -10, 2, 0, 0 },
        { 3, -15, 111,  37, -11, 3, 0, 0 }, { 3, -16, 109,  40, -11, 3, 0, 0 },
        { 3, -16, 108,  42, -12, 3, 0, 0 }, { 4, -17, 106,  45, -13, 3, 0, 0 },
        { 4, -17, 104,  47, -13, 3, 0, 0 }, { 4, -17, 102,  50, -14, 3, 0, 0 },
        { 4, -17, 100,  52, -14, 3, 0, 0 }, { 4, -18,  98,  55, -15, 4, 0, 0 },
        { 4, -18,  96,  58, -15, 3, 0, 0 }, { 4, -18,  94,  60, -16, 4, 0, 0 },
        { 4, -18,  91,  63, -16, 4, 0, 0 }, { 4, -18,  89,  65, -16, 4, 0, 0 },
        { 4, -18,  87,  68, -17, 4, 0, 0 }, { 4, -18,  85,  70, -17, 4, 0, 0 },
        { 4, -18,  82,  73, -17, 4, 0, 0 }, { 4, -18,  80,  75, -17, 4, 0, 0 },
        { 4, -18,  78,  78, -18, 4, 0, 0 }, { 4, -17,  75,  80, -18, 4, 0, 0 },
        { 4, -17,  73,  82, -18, 4, 0, 0 }, { 4, -17,  70,  85, -18, 4, 0, 0 },
        { 4, -17,  68,  87, -18, 4, 0, 0 }, { 4, -16,  65,  89, -18, 4, 0, 0 },
        { 4, -16,  63,  91, -18, 4, 0, 0 }, { 4, -16,  60,  94, -18, 4, 0, 0 },
        { 3, -15,  58,  96, -18, 4, 0, 0 }, { 4, -15,  55,  98, -18, 4, 0, 0 },
        { 3, -14,  52, 100, -17, 4, 0, 0 }, { 3, -14,  50, 102, -17, 4, 0, 0 },
        { 3, -13,  47, 104, -17, 4, 0, 0 }, { 3, -13,  45, 106, -17, 4, 0, 0 },
        { 3, -12,  42, 108, -16, 3, 0, 0 }, { 3, -11,  40, 109, -16, 3, 0, 0 },
        { 3, -11,  37, 111, -15, 3, 0, 0 }, { 2, -10,  35, 113, -15, 3, 0, 0 },
        { 3, -10,  32, 114, -14, 3, 0, 0 }, { 2,  -9,  29, 116, -13, 3, 0, 0 },
        { 2,  -8,  27, 117, -13, 3, 0, 0 }, { 2,  -8,  25, 119, -12, 2, 0, 0 },
        { 2,  -7,  22, 120, -11, 2, 0, 0 }, { 1,  -6,  20, 121, -10, 2, 0, 0 },
        { 1,  -6,  18, 122,  -9, 2, 0, 0 }, { 1,  -5,  15, 123,  -8, 2, 0, 0 },
        { 1,  -4,  13, 124,  -7, 1, 0, 0 }, { 1,  -4,  11, 125,  -6, 1, 0, 0 },
        { 1,  -3,   8, 126,  -5, 1, 0, 0 }, { 1,  -2,   6, 126,  -4, 1, 0, 0 },
        { 0,  -1,   4, 127,  -3, 1, 0, 0 }, { 0,   0,   2, 127,  -1, 0, 0, 0 },
        // [0, 1) — 64 entries
        {  0, 0,   0, 127,   1,   0, 0,  0 }, {  0, 0,  -1, 127,   2,   0, 0,  0 },
        {  0, 1,  -3, 127,   4,  -2, 1,  0 }, {  0, 1,  -5, 127,   6,  -2, 1,  0 },
        {  0, 2,  -6, 126,   8,  -3, 1,  0 }, { -1, 2,  -7, 126,  11,  -4, 2, -1 },
        { -1, 3,  -8, 125,  13,  -5, 2, -1 }, { -1, 3, -10, 124,  16,  -6, 3, -1 },
        { -1, 4, -11, 123,  18,  -7, 3, -1 }, { -1, 4, -12, 122,  20,  -7, 3, -1 },
        { -1, 4, -13, 121,  23,  -8, 3, -1 }, { -2, 5, -14, 120,  25,  -9, 4, -1 },
        { -1, 5, -15, 119,  27, -10, 4, -1 }, { -1, 5, -16, 118,  30, -11, 4, -1 },
        { -2, 6, -17, 116,  33, -12, 5, -1 }, { -2, 6, -17, 114,  35, -12, 5, -1 },
        { -2, 6, -18, 113,  38, -13, 5, -1 }, { -2, 7, -19, 111,  41, -14, 6, -2 },
        { -2, 7, -19, 110,  43, -15, 6, -2 }, { -2, 7, -20, 108,  46, -15, 6, -2 },
        { -2, 7, -20, 106,  49, -16, 6, -2 }, { -2, 7, -21, 104,  51, -16, 7, -2 },
        { -2, 7, -21, 102,  54, -17, 7, -2 }, { -2, 8, -21, 100,  56, -18, 7, -2 },
        { -2, 8, -22,  98,  59, -18, 7, -2 }, { -2, 8, -22,  96,  62, -19, 7, -2 },
        { -2, 8, -22,  94,  64, -19, 7, -2 }, { -2, 8, -22,  91,  67, -20, 8, -2 },
        { -2, 8, -22,  89,  69, -20, 8, -2 }, { -2, 8, -22,  87,  72, -21, 8, -2 },
        { -2, 8, -21,  84,  74, -21, 8, -2 }, { -2, 8, -22,  82,  77, -21, 8, -2 },
        { -2, 8, -21,  79,  79, -21, 8, -2 }, { -2, 8, -21,  77,  82, -22, 8, -2 },
        { -2, 8, -21,  74,  84, -21, 8, -2 }, { -2, 8, -21,  72,  87, -22, 8, -2 },
        { -2, 8, -20,  69,  89, -22, 8, -2 }, { -2, 8, -20,  67,  91, -22, 8, -2 },
        { -2, 7, -19,  64,  94, -22, 8, -2 }, { -2, 7, -19,  62,  96, -22, 8, -2 },
        { -2, 7, -18,  59,  98, -22, 8, -2 }, { -2, 7, -18,  56, 100, -21, 8, -2 },
        { -2, 7, -17,  54, 102, -21, 7, -2 }, { -2, 7, -16,  51, 104, -21, 7, -2 },
        { -2, 6, -16,  49, 106, -20, 7, -2 }, { -2, 6, -15,  46, 108, -20, 7, -2 },
        { -2, 6, -15,  43, 110, -19, 7, -2 }, { -2, 6, -14,  41, 111, -19, 7, -2 },
        { -1, 5, -13,  38, 113, -18, 6, -2 }, { -1, 5, -12,  35, 114, -17, 6, -2 },
        { -1, 5, -12,  33, 116, -17, 6, -2 }, { -1, 4, -11,  30, 118, -16, 5, -1 },
        { -1, 4, -10,  27, 119, -15, 5, -1 }, { -1, 4,  -9,  25, 120, -14, 5, -2 },
        { -1, 3,  -8,  23, 121, -13, 4, -1 }, { -1, 3,  -7,  20, 122, -12, 4, -1 },
        { -1, 3,  -7,  18, 123, -11, 4, -1 }, { -1, 3,  -6,  16, 124, -10, 3, -1 },
        { -1, 2,  -5,  13, 125,  -8, 3, -1 }, { -1, 2,  -4,  11, 126,  -7, 2, -1 },
        {  0, 1,  -3,   8, 126,  -6, 2,  0 }, {  0, 1,  -2,   6, 127,  -5, 1,  0 },
        {  0, 1,  -2,   4, 127,  -3, 1,  0 }, {  0, 0,   0,   2, 127,  -1, 0,  0 },
        // [1, 2) — 64 entries + 1 dummy
        { 0, 0, 0,   1, 127,   0,   0, 0 }, { 0, 0, 0,  -1, 127,   2,   0, 0 },
        { 0, 0, 1,  -3, 127,   4,  -1, 0 }, { 0, 0, 1,  -4, 126,   6,  -2, 1 },
        { 0, 0, 1,  -5, 126,   8,  -3, 1 }, { 0, 0, 1,  -6, 125,  11,  -4, 1 },
        { 0, 0, 1,  -7, 124,  13,  -4, 1 }, { 0, 0, 2,  -8, 123,  15,  -5, 1 },
        { 0, 0, 2,  -9, 122,  18,  -6, 1 }, { 0, 0, 2, -10, 121,  20,  -6, 1 },
        { 0, 0, 2, -11, 120,  22,  -7, 2 }, { 0, 0, 2, -12, 119,  25,  -8, 2 },
        { 0, 0, 3, -13, 117,  27,  -8, 2 }, { 0, 0, 3, -13, 116,  29,  -9, 2 },
        { 0, 0, 3, -14, 114,  32, -10, 3 }, { 0, 0, 3, -15, 113,  35, -10, 2 },
        { 0, 0, 3, -15, 111,  37, -11, 3 }, { 0, 0, 3, -16, 109,  40, -11, 3 },
        { 0, 0, 3, -16, 108,  42, -12, 3 }, { 0, 0, 4, -17, 106,  45, -13, 3 },
        { 0, 0, 4, -17, 104,  47, -13, 3 }, { 0, 0, 4, -17, 102,  50, -14, 3 },
        { 0, 0, 4, -17, 100,  52, -14, 3 }, { 0, 0, 4, -18,  98,  55, -15, 4 },
        { 0, 0, 4, -18,  96,  58, -15, 3 }, { 0, 0, 4, -18,  94,  60, -16, 4 },
        { 0, 0, 4, -18,  91,  63, -16, 4 }, { 0, 0, 4, -18,  89,  65, -16, 4 },
        { 0, 0, 4, -18,  87,  68, -17, 4 }, { 0, 0, 4, -18,  85,  70, -17, 4 },
        { 0, 0, 4, -18,  82,  73, -17, 4 }, { 0, 0, 4, -18,  80,  75, -17, 4 },
        { 0, 0, 4, -18,  78,  78, -18, 4 }, { 0, 0, 4, -17,  75,  80, -18, 4 },
        { 0, 0, 4, -17,  73,  82, -18, 4 }, { 0, 0, 4, -17,  70,  85, -18, 4 },
        { 0, 0, 4, -17,  68,  87, -18, 4 }, { 0, 0, 4, -16,  65,  89, -18, 4 },
        { 0, 0, 4, -16,  63,  91, -18, 4 }, { 0, 0, 4, -16,  60,  94, -18, 4 },
        { 0, 0, 3, -15,  58,  96, -18, 4 }, { 0, 0, 4, -15,  55,  98, -18, 4 },
        { 0, 0, 3, -14,  52, 100, -17, 4 }, { 0, 0, 3, -14,  50, 102, -17, 4 },
        { 0, 0, 3, -13,  47, 104, -17, 4 }, { 0, 0, 3, -13,  45, 106, -17, 4 },
        { 0, 0, 3, -12,  42, 108, -16, 3 }, { 0, 0, 3, -11,  40, 109, -16, 3 },
        { 0, 0, 3, -11,  37, 111, -15, 3 }, { 0, 0, 2, -10,  35, 113, -15, 3 },
        { 0, 0, 3, -10,  32, 114, -14, 3 }, { 0, 0, 2,  -9,  29, 116, -13, 3 },
        { 0, 0, 2,  -8,  27, 117, -13, 3 }, { 0, 0, 2,  -8,  25, 119, -12, 2 },
        { 0, 0, 2,  -7,  22, 120, -11, 2 }, { 0, 0, 1,  -6,  20, 121, -10, 2 },
        { 0, 0, 1,  -6,  18, 122,  -9, 2 }, { 0, 0, 1,  -5,  15, 123,  -8, 2 },
        { 0, 0, 1,  -4,  13, 124,  -7, 1 }, { 0, 0, 1,  -4,  11, 125,  -6, 1 },
        { 0, 0, 1,  -3,   8, 126,  -5, 1 }, { 0, 0, 1,  -2,   6, 126,  -4, 1 },
        { 0, 0, 0,  -1,   4, 127,  -3, 1 }, { 0, 0, 0,   0,   2, 127,  -1, 0 },
        // dummy (replicate row 191)
        { 0, 0, 0,   0,   2, 127,  -1, 0 },
    };

    // ========================================================================
    // Resize Filter
    // ========================================================================

    /// <summary>
    /// Super-resolution resize filter (64 phases × 8 taps).
    /// Maps to dav1d_resize_filter[64][8].
    /// Note: values include negated center tap (stored as negative).
    /// </summary>
    public static readonly sbyte[,] ResizeFilter = new sbyte[64, 8]
    {
        { 0,  0,  0, -128,    0,  0,  0, 0 }, { 0,  0,  1, -128,   -2,  1,  0, 0 },
        { 0, -1,  3, -127,   -4,  2, -1, 0 }, { 0, -1,  4, -127,   -6,  3, -1, 0 },
        { 0, -2,  6, -126,   -8,  3, -1, 0 }, { 0, -2,  7, -125,  -11,  4, -1, 0 },
        { 1, -2,  8, -125,  -13,  5, -2, 0 }, { 1, -3,  9, -124,  -15,  6, -2, 0 },
        { 1, -3, 10, -123,  -18,  6, -2, 1 }, { 1, -3, 11, -122,  -20,  7, -3, 1 },
        { 1, -4, 12, -121,  -22,  8, -3, 1 }, { 1, -4, 13, -120,  -25,  9, -3, 1 },
        { 1, -4, 14, -118,  -28,  9, -3, 1 }, { 1, -4, 15, -117,  -30, 10, -4, 1 },
        { 1, -5, 16, -116,  -32, 11, -4, 1 }, { 1, -5, 16, -114,  -35, 12, -4, 1 },
        { 1, -5, 17, -112,  -38, 12, -4, 1 }, { 1, -5, 18, -111,  -40, 13, -5, 1 },
        { 1, -5, 18, -109,  -43, 14, -5, 1 }, { 1, -6, 19, -107,  -45, 14, -5, 1 },
        { 1, -6, 19, -105,  -48, 15, -5, 1 }, { 1, -6, 19, -103,  -51, 16, -5, 1 },
        { 1, -6, 20, -101,  -53, 16, -6, 1 }, { 1, -6, 20,  -99,  -56, 17, -6, 1 },
        { 1, -6, 20,  -97,  -58, 17, -6, 1 }, { 1, -6, 20,  -95,  -61, 18, -6, 1 },
        { 2, -7, 20,  -93,  -64, 18, -6, 2 }, { 2, -7, 20,  -91,  -66, 19, -6, 1 },
        { 2, -7, 20,  -88,  -69, 19, -6, 1 }, { 2, -7, 20,  -86,  -71, 19, -6, 1 },
        { 2, -7, 20,  -84,  -74, 20, -7, 2 }, { 2, -7, 20,  -81,  -76, 20, -7, 1 },
        { 2, -7, 20,  -79,  -79, 20, -7, 2 }, { 1, -7, 20,  -76,  -81, 20, -7, 2 },
        { 2, -7, 20,  -74,  -84, 20, -7, 2 }, { 1, -6, 19,  -71,  -86, 20, -7, 2 },
        { 1, -6, 19,  -69,  -88, 20, -7, 2 }, { 1, -6, 19,  -66,  -91, 20, -7, 2 },
        { 2, -6, 18,  -64,  -93, 20, -7, 2 }, { 1, -6, 18,  -61,  -95, 20, -6, 1 },
        { 1, -6, 17,  -58,  -97, 20, -6, 1 }, { 1, -6, 17,  -56,  -99, 20, -6, 1 },
        { 1, -6, 16,  -53, -101, 20, -6, 1 }, { 1, -5, 16,  -51, -103, 19, -6, 1 },
        { 1, -5, 15,  -48, -105, 19, -6, 1 }, { 1, -5, 14,  -45, -107, 19, -6, 1 },
        { 1, -5, 14,  -43, -109, 18, -5, 1 }, { 1, -5, 13,  -40, -111, 18, -5, 1 },
        { 1, -4, 12,  -38, -112, 17, -5, 1 }, { 1, -4, 12,  -35, -114, 16, -5, 1 },
        { 1, -4, 11,  -32, -116, 16, -5, 1 }, { 1, -4, 10,  -30, -117, 15, -4, 1 },
        { 1, -3,  9,  -28, -118, 14, -4, 1 }, { 1, -3,  9,  -25, -120, 13, -4, 1 },
        { 1, -3,  8,  -22, -121, 12, -4, 1 }, { 1, -3,  7,  -20, -122, 11, -3, 1 },
        { 1, -2,  6,  -18, -123, 10, -3, 1 }, { 0, -2,  6,  -15, -124,  9, -3, 1 },
        { 0, -2,  5,  -13, -125,  8, -2, 1 }, { 0, -1,  4,  -11, -125,  7, -2, 0 },
        { 0, -1,  3,   -8, -126,  6, -2, 0 }, { 0, -1,  3,   -6, -127,  4, -1, 0 },
        { 0, -1,  2,   -4, -127,  3, -1, 0 }, { 0,  0,  1,   -2, -128,  1,  0, 0 },
    };

    // ========================================================================
    // Smooth Prediction Weights
    // ========================================================================

    /// <summary>
    /// Smooth intra prediction weights, indexed by block size offset.
    /// Always offset by block size (minimum 2).
    /// Maps to dav1d_sm_weights[128].
    /// </summary>
    public static readonly byte[] SmoothWeights = new byte[128]
    {
        // Unused (offset 0, 1)
          0,   0,
        // bs = 2
        255, 128,
        // bs = 4
        255, 149,  85,  64,
        // bs = 8
        255, 197, 146, 105,  73,  50,  37,  32,
        // bs = 16
        255, 225, 196, 170, 145, 123, 102,  84,
         68,  54,  43,  33,  26,  20,  17,  16,
        // bs = 32
        255, 240, 225, 210, 196, 182, 169, 157,
        145, 133, 122, 111, 101,  92,  83,  74,
         66,  59,  52,  45,  39,  34,  29,  25,
         21,  17,  14,  12,  10,   9,   8,   8,
        // bs = 64
        255, 248, 240, 233, 225, 218, 210, 203,
        196, 189, 182, 176, 169, 163, 156, 150,
        144, 138, 133, 127, 121, 116, 111, 106,
        101,  96,  91,  86,  82,  77,  73,  69,
         65,  61,  57,  54,  50,  47,  44,  41,
         38,  35,  32,  29,  27,  25,  22,  20,
         18,  16,  15,  13,  12,  10,   9,   8,
          7,   6,   6,   5,   5,   4,   4,   4,
    };

    // ========================================================================
    // Directional Intra Prediction Derivative
    // ========================================================================

    /// <summary>
    /// Directional intra prediction angle derivatives.
    /// Indexed by angle offset (0..43). Values of 0 are unused.
    /// Maps to dav1d_dr_intra_derivative[44].
    /// </summary>
    public static readonly ushort[] DrIntraDerivative = new ushort[44]
    {
              0,    // 0
        1023, 0,    // 3, 93, 183
         547,       // 6, 96, 186
         372, 0, 0, // 9, 99, 189
         273,       // 14, 104, 194
         215, 0,    // 17, 107, 197
         178,       // 20, 110, 200
         151, 0,    // 23, 113, 203
         132,       // 26, 116, 206
         116, 0,    // 29, 119, 209
         102, 0,    // 32, 122, 212
          90,       // 36, 126, 216
          80, 0,    // 39, 129, 219
          71,       // 42, 132, 222
          64, 0,    // 45, 135, 225
          57,       // 48, 138, 228
          51, 0,    // 51, 141, 231
          45, 0,    // 54, 144, 234
          40,       // 58, 148, 238
          35, 0,    // 61, 151, 241
          31,       // 64, 154, 244
          27, 0,    // 67, 157, 247
          23,       // 70, 160, 250
          19, 0,    // 73, 163, 253
          15, 0,    // 76, 166, 256
          11, 0,    // 81, 171, 261
           7,       // 84, 174, 264
           3,       // 87, 177, 267
    };

    // ========================================================================
    // Filter Intra Taps
    // ========================================================================

    /// <summary>
    /// Filter intra prediction taps.
    /// Indexed by [mode 0..4][position 0..63].
    /// Non-x86 layout: 7 groups of 8 values interleaved, last 8 unused.
    /// For position p and tap t: taps[mode][t*8 + p] where t=0..6, p=0..7.
    /// Maps to dav1d_filter_intra_taps[5][64].
    /// </summary>
    public static readonly sbyte[,] FilterIntraTaps = new sbyte[5, 64]
    {
        {
            // Group 0 (tap 0): positions 0..7
            -6, -5, -3, -3, -4, -3, -3, -3,
            // Group 1 (tap 1): positions 0..7
            10,  2,  1,  1,  6,  2,  2,  1,
            // Group 2 (tap 2): positions 0..7
             0, 10,  1,  1,  0,  6,  2,  2,
            // Group 3 (tap 3): positions 0..7
             0,  0, 10,  2,  0,  0,  6,  2,
            // Group 4 (tap 4): positions 0..7
             0,  0,  0, 10,  0,  0,  0,  6,
            // Group 5 (tap 5): positions 0..7
            12,  9,  7,  5,  2,  2,  2,  3,
            // Group 6 (tap 6): positions 0..7
             0,  0,  0,  0, 12,  9,  7,  5,
            // Padding (unused)
             0,  0,  0,  0,  0,  0,  0,  0,
        },
        {
            -10, -6, -4, -2, -10, -6, -4, -2,
             16,  0,  0,  0,  16,  0,  0,  0,
              0, 16,  0,  0,   0, 16,  0,  0,
              0,  0, 16,  0,   0,  0, 16,  0,
              0,  0,  0, 16,   0,  0,  0, 16,
             10,  6,  4,  2,   0,  0,  0,  0,
              0,  0,  0,  0,  10,  6,  4,  2,
              0,  0,  0,  0,   0,  0,  0,  0,
        },
        {
            -8, -8, -8, -8, -4, -4, -4, -4,
             8,  0,  0,  0,  4,  0,  0,  0,
             0,  8,  0,  0,  0,  4,  0,  0,
             0,  0,  8,  0,  0,  0,  4,  0,
             0,  0,  0,  8,  0,  0,  0,  4,
            16, 16, 16, 16,  0,  0,  0,  0,
             0,  0,  0,  0, 16, 16, 16, 16,
             0,  0,  0,  0,  0,  0,  0,  0,
        },
        {
            -2, -1, -1,  0, -1, -1, -1, -1,
             8,  3,  2,  1,  4,  3,  2,  2,
             0,  8,  3,  2,  0,  4,  3,  2,
             0,  0,  8,  3,  0,  0,  4,  3,
             0,  0,  0,  8,  0,  0,  0,  4,
            10,  6,  4,  2,  3,  4,  4,  3,
             0,  0,  0,  0, 10,  6,  4,  3,
             0,  0,  0,  0,  0,  0,  0,  0,
        },
        {
            -12, -10, -9, -8, -10, -9, -8, -7,
             14,   0,  0,  0,  12,  1,  0,  0,
              0,  14,  0,  0,   0, 12,  0,  0,
              0,   0, 14,  0,   0,  0, 12,  1,
              0,   0,  0, 14,   0,  0,  0, 12,
             14,  12, 11, 10,   0,  0,  1,  1,
              0,   0,  0,  0,  14, 12, 11,  9,
              0,   0,  0,  0,   0,  0,  0,  0,
        },
    };

    // ========================================================================
    // OBMC Masks
    // ========================================================================

    /// <summary>
    /// Overlapped block motion compensation masks.
    /// Indexed by position offset (block size dependent).
    /// Maps to dav1d_obmc_masks[64].
    /// </summary>
    public static readonly byte[] ObmcMasks = new byte[64]
    {
        // Unused
         0,  0,
        // 2
        19,  0,
        // 4
        25, 14,  5,  0,
        // 8
        28, 22, 16, 11,  7,  3,  0,  0,
        // 16
        30, 27, 24, 21, 18, 15, 12, 10,  8,  6,  4,  3,  0,  0,  0,  0,
        // 32
        31, 29, 28, 26, 24, 23, 21, 20, 19, 17, 16, 14, 13, 12, 11,  9,
         8,  7,  6,  5,  4,  4,  3,  2,  0,  0,  0,  0,  0,  0,  0,  0,
    };

    // ========================================================================
    // Allowed Feature Masks
    // ========================================================================

    /// <summary>
    /// Bitmask of block sizes that allow CFL (chroma-from-luma) prediction.
    /// Test with: (CflAllowedMask >> (int)blockSize) &amp; 1.
    /// Maps to dav1d cfl_allowed_mask (tables.h).
    /// </summary>
    public const uint CflAllowedMask =
        (1u << (int)Av1BlockSize.Bs32x32) |
        (1u << (int)Av1BlockSize.Bs32x16) |
        (1u << (int)Av1BlockSize.Bs32x8) |
        (1u << (int)Av1BlockSize.Bs16x32) |
        (1u << (int)Av1BlockSize.Bs16x16) |
        (1u << (int)Av1BlockSize.Bs16x8) |
        (1u << (int)Av1BlockSize.Bs16x4) |
        (1u << (int)Av1BlockSize.Bs8x32) |
        (1u << (int)Av1BlockSize.Bs8x16) |
        (1u << (int)Av1BlockSize.Bs8x8) |
        (1u << (int)Av1BlockSize.Bs8x4) |
        (1u << (int)Av1BlockSize.Bs4x16) |
        (1u << (int)Av1BlockSize.Bs4x8) |
        (1u << (int)Av1BlockSize.Bs4x4);

    /// <summary>
    /// Bitmask of block sizes that allow wedge compound prediction.
    /// Test with: (WedgeAllowedMask >> (int)blockSize) &amp; 1.
    /// Maps to dav1d wedge_allowed_mask (tables.h).
    /// </summary>
    public const uint WedgeAllowedMask =
        (1u << (int)Av1BlockSize.Bs32x32) |
        (1u << (int)Av1BlockSize.Bs32x16) |
        (1u << (int)Av1BlockSize.Bs32x8) |
        (1u << (int)Av1BlockSize.Bs16x32) |
        (1u << (int)Av1BlockSize.Bs16x16) |
        (1u << (int)Av1BlockSize.Bs16x8) |
        (1u << (int)Av1BlockSize.Bs8x32) |
        (1u << (int)Av1BlockSize.Bs8x16) |
        (1u << (int)Av1BlockSize.Bs8x8);

    /// <summary>
    /// Bitmask of block sizes that allow inter-intra compound prediction.
    /// Test with: (InterIntraAllowedMask >> (int)blockSize) &amp; 1.
    /// Maps to dav1d interintra_allowed_mask (tables.h).
    /// </summary>
    public const uint InterIntraAllowedMask =
        (1u << (int)Av1BlockSize.Bs32x32) |
        (1u << (int)Av1BlockSize.Bs32x16) |
        (1u << (int)Av1BlockSize.Bs16x32) |
        (1u << (int)Av1BlockSize.Bs16x16) |
        (1u << (int)Av1BlockSize.Bs16x8) |
        (1u << (int)Av1BlockSize.Bs8x16) |
        (1u << (int)Av1BlockSize.Bs8x8);

    // ========================================================================
    // Y-Mode Size Context
    // ========================================================================

    /// <summary>
    /// Maps block size to Y-mode CDF context index (0..3).
    /// Used when selecting CDF for intra luma mode in inter frames.
    /// Maps to dav1d_ymode_size_context[N_BS_SIZES].
    /// </summary>
    public static readonly byte[] YmodeSizeContext = new byte[22]
    {
        // 128x128, 128x64, 64x128, 64x64, 64x32, 64x16,
           3,       3,      3,      3,     3,     2,
        // 32x64,   32x32,  32x16,  32x8,
           3,       3,      2,     1,
        // 16x64,   16x32,  16x16,  16x8,  16x4,
           2,       2,      2,     1,    0,
        // 8x32,    8x16,   8x8,    8x4,
           1,       1,      1,     0,
        // 4x16,    4x8,    4x4
           0,       0,      0,
    };
}
