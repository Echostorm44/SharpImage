using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>YV12_BUFFER_CONFIG for 8-bit planes: buffers with a border, crop sizes (the frame size) per plane type.</summary>
internal sealed class AomFrameBuffer
{
    public const int Border = 288;   // AOM_BORDER_IN_PIXELS (libaom's encoder allocates 288 for all-intra; only in-frame
                                     // pixels are ever read by the intra search)
    public readonly byte[][] Buffers = new byte[3][];
    /// <summary>High bit depth (BitDepth &gt; 8) planes (CONVERT_TO_SHORTPTR buffers); Buffers' entries stay null then.</summary>
    public readonly ushort[][] Buffers16 = new ushort[3][];
    public readonly int BitDepth;
    public bool Hbd => BitDepth > 8;
    public readonly int[] Offsets = new int[3];   // index of the (0, 0) sample in Buffers[p]
    public readonly int[] Strides = new int[3];
    public readonly int[] CropWidths = new int[2], CropHeights = new int[2];
    public readonly int SsX, SsY, NumPlanes;

    /// <summary>Gives the planes back to AomBufferPool (the frame is unusable afterwards).</summary>
    public void Release()
    {
        for (int p = 0; p < 3; p++) { AomBufferPool.Return(Buffers[p]); AomBufferPool.Return(Buffers16[p]); Buffers[p] = null!; Buffers16[p] = null!; }
    }

    public AomFrameBuffer(int width, int height, int ssX, int ssY, bool monochrome, int bitDepth = 8)
    {
        SsX = ssX; SsY = ssY;
        BitDepth = bitDepth;
        NumPlanes = monochrome ? 1 : 3;
        CropWidths[0] = width; CropHeights[0] = height;
        CropWidths[1] = (width + ssX) >> ssX; CropHeights[1] = (height + ssY) >> ssY;
        // aligned to 8 like libaom's aligned_width / aligned_height, plus borders
        int alignedW = (width + 7) & ~7, alignedH = (height + 7) & ~7;
        for (int p = 0; p < NumPlanes; p++)
        {
            int sx = p == 0 ? 0 : ssX, sy = p == 0 ? 0 : ssY;
            int border = Border >> sx, borderY = Border >> sy;
            int w = (alignedW >> sx) + 2 * border, h = (alignedH >> sy) + 2 * borderY;
            Strides[p] = (w + 31) & ~31;
            if (bitDepth > 8) Buffers16[p] = AomBufferPool.Rent<ushort>(Strides[p] * h);
            else Buffers[p] = AomBufferPool.Rent<byte>(Strides[p] * h);
            Offsets[p] = borderY * Strides[p] + border;
        }
    }
}

/// <summary>AV1_COMMON / CommonModeInfoParams (the parts the all-intra encoder reads): frame size in mi units, the mi
/// grid (MB_MODE_INFO pointers per 4x4) and its allocation (one MB_MODE_INFO per 4x4: mi_alloc_bsize BLOCK_4X4), the
/// frame tx type map, the above contexts, the reconstruction (cur_frame) and the tile.</summary>
internal sealed partial class AomCommon
{
    public readonly int Width, Height;
    public readonly int MiRows, MiCols, MiStride;
    public readonly AomMbModeInfo?[] MiGridBase;
    public readonly AomMbModeInfo[] MiAlloc;
    public readonly byte[] TxTypeMap;
    public readonly AomFrameBuffer CurFrame;
    public readonly int SsX, SsY;
    public readonly bool Monochrome;
    public int NumPlanes => Monochrome ? 1 : 3;
    // sequence: sb_size, mib_size (log2)
    public int SbSize = BLOCK_64X64, MibSize = 16, MibSizeLog2 = 4;
    // above contexts (cm->above_contexts: one set per tile row): entropy per plane, partition, txfm
    public byte[][][] AboveEntropy = Array.Empty<byte[][]>();
    public byte[][] AbovePartition = Array.Empty<byte[]>();
    public byte[][] AboveTxfm = Array.Empty<byte[]>();
    // cm->tiles (CommonTileParams, uniform spacing)
    public int TileCols = 1, TileRows = 1, Log2Cols, Log2Rows, MinLog2Cols, MaxLog2Cols, MinLog2Rows, MaxLog2Rows, MinLog2, MaxWidthSb;
    public int[] ColStartSb = Array.Empty<int>(), RowStartSb = Array.Empty<int>();
    // frame-level CDFs (cm->fc) and qindex
    public Av1CdfContext Fc = null!;
    public int BaseQindex;
    // cm->quant_params: the frame's delta q values and quantization matrix levels
    public int YDcDeltaQ, UDcDeltaQ, UAcDeltaQ, VDcDeltaQ, VAcDeltaQ;
    public bool UsingQmatrix;
    public int QmLevelY = 15, QmLevelU = 15, QmLevelV = 15;

    public readonly int BitDepth;

    public AomCommon(int width, int height, int ssX, int ssY, bool monochrome, int sbSize = BLOCK_64X64, int bitDepth = 8)
    {
        BitDepth = bitDepth;
        SbSize = sbSize;
        MibSize = MiSizeWide[sbSize];
        MibSizeLog2 = MiSizeWideLog2[sbSize];
        Width = width; Height = height; SsX = ssX; SsY = ssY; Monochrome = monochrome;
        // enc_set_mb_mi: the mi grid covers the frame aligned to 8 luma pixels (mi_cols / mi_rows are always even)
        MiCols = ((width + 7) & ~7) >> 2;
        MiRows = ((height + 7) & ~7) >> 2;
        MiStride = (MiCols + 31) & ~31;   // calc_mi_size: aligned to MAX_MIB_SIZE
        int alignedRows = (MiRows + 31) & ~31;
        MiGridBase = AomBufferPool.Rent<AomMbModeInfo?>(MiStride * alignedRows);
        MiAlloc = new AomMbModeInfo[MiStride * alignedRows];
        for (int i = 0; i < MiAlloc.Length; i++) MiAlloc[i] = new AomMbModeInfo();
        TxTypeMap = AomBufferPool.Rent<byte>(MiStride * alignedRows);
        CurFrame = new AomFrameBuffer(width, height, ssX, ssY, monochrome, bitDepth);
        SetTileInfo(0, 0);
    }

    /// <summary>Gives the mi grid, the tx type map and the reconstruction back to AomBufferPool (unusable afterwards).</summary>
    public void Release()
    {
        AomBufferPool.Return(MiGridBase);
        AomBufferPool.Return(TxTypeMap);
        CurFrame.Release();
    }

    // ---- tiles (av1/common/tile_common.c, encoder.c set_tile_info) ------------------------------------------------

    private const int MAX_TILE_WIDTH = 4096, MAX_TILE_AREA = 4096 * 2304, MAX_TILE_ROWS = 64, MAX_TILE_COLS = 64;

    /// <summary>tile_log2: the smallest k &gt;= 0 with (blkSize &lt;&lt; k) &gt;= target.</summary>
    private static int TileLog2(int blkSize, int target)
    {
        int k = 0;
        for (; (blkSize << k) < target; k++) { }
        return k;
    }

    /// <summary>set_tile_info for tile_cfg's tile_columns / tile_rows (log2, uniform spacing: no explicit tile widths /
    /// heights, no superres): av1_get_tile_limits, the column and row log2s clamped to the limits,
    /// av1_calculate_tile_cols / av1_calculate_tile_rows; then av1_alloc_above_context_buffer's per-tile-row above
    /// contexts.</summary>
    public void SetTileInfo(int tileColumns, int tileRows)
    {
        int sbCols = (MiCols + MibSize - 1) >> MibSizeLog2, sbRows = (MiRows + MibSize - 1) >> MibSizeLog2;
        // av1_get_tile_limits
        int sbSizeLog2 = MibSizeLog2 + 2;
        MaxWidthSb = MAX_TILE_WIDTH >> sbSizeLog2;
        int maxTileAreaSb = MAX_TILE_AREA >> (2 * sbSizeLog2);
        MinLog2Cols = TileLog2(MaxWidthSb, sbCols);
        MaxLog2Cols = TileLog2(1, Math.Min(sbCols, MAX_TILE_COLS));
        MaxLog2Rows = TileLog2(1, Math.Min(sbRows, MAX_TILE_ROWS));
        MinLog2 = TileLog2(maxTileAreaSb, sbCols * sbRows);
        MinLog2 = Math.Max(MinLog2, MinLog2Cols);
        // configure tile columns
        Log2Cols = Math.Max(tileColumns, MinLog2Cols);
        int minLog2Cols = 0;
        for (; (MaxWidthSb << minLog2Cols) <= sbCols; ++minLog2Cols) { }
        Log2Cols = Math.Max(Log2Cols, minLog2Cols);
        Log2Cols = Math.Min(Log2Cols, MaxLog2Cols);
        // av1_calculate_tile_cols (uniform spacing)
        int sizeSb = (sbCols + (1 << Log2Cols) - 1) >> Log2Cols;
        var colStart = new System.Collections.Generic.List<int>();
        for (int startSb = 0; startSb < sbCols; startSb += sizeSb) colStart.Add(startSb);
        TileCols = colStart.Count;
        colStart.Add(sbCols);
        ColStartSb = colStart.ToArray();
        MinLog2Rows = Math.Max(MinLog2 - Log2Cols, 0);
        // configure tile rows
        Log2Rows = Math.Max(tileRows, MinLog2Rows);
        Log2Rows = Math.Min(Log2Rows, MaxLog2Rows);
        // av1_calculate_tile_rows
        sizeSb = (sbRows + (1 << Log2Rows) - 1) >> Log2Rows;
        var rowStart = new System.Collections.Generic.List<int>();
        for (int startSb = 0; startSb < sbRows; startSb += sizeSb) rowStart.Add(startSb);
        TileRows = rowStart.Count;
        rowStart.Add(sbRows);
        RowStartSb = rowStart.ToArray();

        // av1_alloc_above_context_buffer: one set of above contexts per tile row, aligned to the superblock size
        int alignedMiCols = (MiCols + 31) & ~31;
        AboveEntropy = new byte[TileRows][][];
        AbovePartition = new byte[TileRows][];
        AboveTxfm = new byte[TileRows][];
        for (int r = 0; r < TileRows; r++)
        {
            AboveEntropy[r] = new byte[3][];
            for (int p = 0; p < 3; p++) AboveEntropy[r][p] = new byte[alignedMiCols];
            AbovePartition[r] = new byte[alignedMiCols];
            AboveTxfm[r] = new byte[alignedMiCols];
        }
    }

    /// <summary>av1_tile_init (av1_tile_set_row / av1_tile_set_col).</summary>
    public AomTileInfo TileInit(int row, int col) => new()
    {
        TileRow = row, TileCol = col,
        MiRowStart = RowStartSb[row] << MibSizeLog2, MiRowEnd = Math.Min(RowStartSb[row + 1] << MibSizeLog2, MiRows),
        MiColStart = ColStartSb[col] << MibSizeLog2, MiColEnd = Math.Min(ColStartSb[col + 1] << MibSizeLog2, MiCols),
    };

    /// <summary>av1_zero_above_context for the tile columns [miColStart, miColEnd) of a tile row.</summary>
    public void ZeroAboveContext(int miColStart, int miColEnd, int tileRow)
    {
        int width = miColEnd - miColStart;
        int alignedWidth = (width + MibSize - 1) & ~(MibSize - 1);
        int offsetY = miColStart, widthY = alignedWidth;
        int offsetUv = offsetY >> SsX, widthUv = widthY >> SsX;
        var e = AboveEntropy[tileRow];
        Array.Clear(e[0], offsetY, widthY);
        if (NumPlanes > 1)
        {
            Array.Clear(e[1], offsetUv, widthUv);
            Array.Clear(e[2], offsetUv, widthUv);
        }
        Array.Clear(AbovePartition[tileRow], miColStart, alignedWidth);
        Array.Fill(AboveTxfm[tileRow], (byte)TxSizeWide[TX_64X64], miColStart, alignedWidth);
    }
}

/// <summary>TileInfo (av1/common/tile_common.h).</summary>
internal sealed class AomTileInfo
{
    public int MiRowStart, MiRowEnd, MiColStart, MiColEnd, TileRow, TileCol;

    public int SbRows(AomCommon cm) => (MiRowEnd - MiRowStart + cm.MibSize - 1) >> cm.MibSizeLog2;   // av1_get_sb_rows_in_tile
    public int SbCols(AomCommon cm) => (MiColEnd - MiColStart + cm.MibSize - 1) >> cm.MibSizeLog2;   // av1_get_sb_cols_in_tile
}
