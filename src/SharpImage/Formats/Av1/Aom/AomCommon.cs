using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>YV12_BUFFER_CONFIG for 8-bit planes: buffers with a border, crop sizes (the frame size) per plane type.</summary>
internal sealed class AomFrameBuffer
{
    public const int Border = 288;   // AOM_BORDER_IN_PIXELS (libaom's encoder allocates 288 for all-intra; only in-frame
                                     // pixels are ever read by the intra search)
    public readonly byte[][] Buffers = new byte[3][];
    public readonly int[] Offsets = new int[3];   // index of the (0, 0) sample in Buffers[p]
    public readonly int[] Strides = new int[3];
    public readonly int[] CropWidths = new int[2], CropHeights = new int[2];
    public readonly int SsX, SsY, NumPlanes;

    public AomFrameBuffer(int width, int height, int ssX, int ssY, bool monochrome)
    {
        SsX = ssX; SsY = ssY;
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
            Buffers[p] = new byte[Strides[p] * h];
            Offsets[p] = borderY * Strides[p] + border;
        }
    }
}

/// <summary>AV1_COMMON / CommonModeInfoParams (the parts the all-intra encoder reads): frame size in mi units, the mi
/// grid (MB_MODE_INFO pointers per 4x4) and its allocation (one MB_MODE_INFO per 4x4: mi_alloc_bsize BLOCK_4X4), the
/// frame tx type map, the above contexts, the reconstruction (cur_frame) and the tile.</summary>
internal sealed class AomCommon
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
    // above contexts (one tile row): entropy per plane, partition, txfm
    public readonly byte[][] AboveEntropy = new byte[3][];
    public readonly byte[] AbovePartition;
    public readonly byte[] AboveTxfm;
    // the tile (one tile)
    public int TileMiRowStart, TileMiRowEnd, TileMiColStart, TileMiColEnd;
    // frame-level CDFs (cm->fc) and qindex
    public Av1CdfContext Fc = null!;
    public int BaseQindex;
    // cm->quant_params: the frame's delta q values and quantization matrix levels
    public int YDcDeltaQ, UDcDeltaQ, UAcDeltaQ, VDcDeltaQ, VAcDeltaQ;
    public bool UsingQmatrix;
    public int QmLevelY = 15, QmLevelU = 15, QmLevelV = 15;

    public AomCommon(int width, int height, int ssX, int ssY, bool monochrome, int sbSize = BLOCK_64X64)
    {
        SbSize = sbSize;
        MibSize = MiSizeWide[sbSize];
        MibSizeLog2 = MiSizeWideLog2[sbSize];
        Width = width; Height = height; SsX = ssX; SsY = ssY; Monochrome = monochrome;
        // enc_set_mb_mi: the mi grid covers the frame aligned to 8 luma pixels (mi_cols / mi_rows are always even)
        MiCols = ((width + 7) & ~7) >> 2;
        MiRows = ((height + 7) & ~7) >> 2;
        MiStride = (MiCols + 31) & ~31;   // calc_mi_size: aligned to MAX_MIB_SIZE
        int alignedRows = (MiRows + 31) & ~31;
        MiGridBase = new AomMbModeInfo?[MiStride * alignedRows];
        MiAlloc = new AomMbModeInfo[MiStride * alignedRows];
        for (int i = 0; i < MiAlloc.Length; i++) MiAlloc[i] = new AomMbModeInfo();
        TxTypeMap = new byte[MiStride * alignedRows];
        CurFrame = new AomFrameBuffer(width, height, ssX, ssY, monochrome);
        int alignedMiCols = (MiCols + 31) & ~31;
        for (int p = 0; p < 3; p++) AboveEntropy[p] = new byte[alignedMiCols];
        AbovePartition = new byte[alignedMiCols];
        AboveTxfm = new byte[alignedMiCols];
        TileMiRowStart = 0; TileMiRowEnd = MiRows; TileMiColStart = 0; TileMiColEnd = MiCols;
    }

    /// <summary>av1_zero_above_context (whole tile width).</summary>
    public void ZeroAboveContext()
    {
        for (int p = 0; p < 3; p++) Array.Clear(AboveEntropy[p]);
        Array.Clear(AbovePartition);
        Array.Fill(AboveTxfm, (byte)TxSizeWide[TX_64X64]);
    }
}
