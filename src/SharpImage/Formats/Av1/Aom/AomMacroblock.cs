using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>MACROBLOCKD (av1/common/blockd.h): the decoder-side view of the current block.</summary>
internal sealed partial class AomMacroblockD
{
    public const int MaxMibSize = 32;          // MAX_MIB_SIZE (128 / 4)
    public int MiRow, MiCol;
    public int MiStride;
    public bool IsChromaRef;
    public readonly AomMbdPlane[] Plane = { new(), new(), new() };
    // tile (TileInfo): mi bounds
    public int TileMiRowStart, TileMiRowEnd, TileMiColStart, TileMiColEnd;
    // mi grid: xd->mi = &mi_grid[mi_row * stride + mi_col]; Mi0 = xd->mi[0]
    public AomMbModeInfo?[] MiGrid = Array.Empty<AomMbModeInfo?>();
    public int MiOffset;
    public AomMbModeInfo Mi0 = null!;
    public bool UpAvailable, LeftAvailable, ChromaUpAvailable, ChromaLeftAvailable;
    public AomMbModeInfo? LeftMbmi, AboveMbmi, ChromaLeftMbmi, ChromaAboveMbmi;
    public byte[] TxTypeMap = Array.Empty<byte>();
    public int TxTypeMapOffset;
    public int TxTypeMapStride;
    public int MbToLeftEdge, MbToRightEdge, MbToTopEdge, MbToBottomEdge;
    // entropy contexts: above per plane (tile row arrays), left per plane (SB-local)
    public readonly byte[][] AboveEntropyContext = new byte[3][];
    public readonly byte[][] LeftEntropyContext = { new byte[MaxMibSize], new byte[MaxMibSize], new byte[MaxMibSize] };
    public byte[] AbovePartitionContext = Array.Empty<byte>();
    public readonly byte[] LeftPartitionContext = new byte[MaxMibSize];
    public byte[] AboveTxfmContext = Array.Empty<byte>();
    public int AboveTxfmContextOffset;
    public readonly byte[] LeftTxfmContextBuffer = new byte[MaxMibSize];
    public int LeftTxfmContextOffset;
    public int Width, Height;   // block width / height in MB_MODE_INFO (4x4) units
    public int Bd = 8;
    public readonly int[] Qindex = new int[8];
    public readonly int[] Lossless = new int[8];
    public int CurrentBaseQindex;
    public readonly AomCflCtx Cfl = new();

    /// <summary>The MB_MODE_INFO at (row, col) mi offset from the current block's top-left.</summary>
    public AomMbModeInfo? MiAt(int dr, int dc) => MiGrid[MiOffset + dr * MiStride + dc];
}

/// <summary>CFL_CTX (av1/common/blockd.h).</summary>
internal sealed partial class AomCflCtx
{
    public const int CflBufLine = 32, CflBufSquare = CflBufLine * CflBufLine;
    public readonly ushort[] ReconBufQ3 = new ushort[CflBufSquare];
    public readonly short[] AcBufQ3 = new short[CflBufSquare];
    public readonly ushort[] DcPredCache0 = new ushort[CflBufLine], DcPredCache1 = new ushort[CflBufLine];
    public readonly bool[] DcPredIsCached = new bool[2];
    public bool UseDcPredCache;
    public int BufHeight, BufWidth;
    public bool AreParametersComputed;
    public int SubsamplingX, SubsamplingY;
    public bool IsChromaReference;
    public int StoreY;
}

/// <summary>ModeCosts (av1/encoder/block.h): the intra key-frame mode symbol costs.</summary>
internal sealed class AomModeCosts
{
    public const int PartitionContexts = 20, SkipContexts = 3, KfModeContexts = 5, IntraModes = 13, UvIntraModes = 14,
        BlockSizesAll = 22, PalatteBsizeCtxs = 7, PaletteYModeContexts = 3, PaletteUvModeContexts = 2, PaletteSizes = 7,
        PaletteColorIndexContexts = 5, PaletteColors = 8, CflJointSigns = 8, CflAlphabetSize = 16, MaxTxCats = 4,
        TxSizeContexts = 3, MaxTxDepth = 2, TxfmPartitionContexts = 21, ExtTxSizes = 4, ExtTxSetsIntra = 3, ExtTxSetsInter = 4, TxTypes = 16,
        DirectionalModes = 8, MaxAngleDelta = 3, FilterIntraModes = 5, DeltaLfProbs = 3, SpatialPredictionProbs = 3,
        SegTemporalPredCtxs = 3, IntrabcSymbols = 2, RestoreSwitchableTypes = 3;

    public readonly int[] PartitionCost = new int[PartitionContexts * 10];                      // [ctx][EXT_PARTITION_TYPES]
    public readonly int[] SkipTxfmCost = new int[SkipContexts * 2];                            // [ctx][2]
    public readonly int[] YModeCosts = new int[IntraModes * IntraModes * IntraModes];            // [above ctx][left ctx][mode] (libaom's 13x13x13)
    public readonly int[] IntraUvModeCost = new int[2 * IntraModes * UvIntraModes];            // [cfl_allowed][y mode][uv mode]
    public readonly int[] FilterIntraModeCost = new int[FilterIntraModes];
    public readonly int[] FilterIntraCost = new int[BlockSizesAll * 2];
    public readonly int[] PaletteYSizeCost = new int[PalatteBsizeCtxs * PaletteSizes];
    public readonly int[] PaletteUvSizeCost = new int[PalatteBsizeCtxs * PaletteSizes];
    public readonly int[] PaletteYModeCost = new int[PalatteBsizeCtxs * PaletteYModeContexts * 2];
    public readonly int[] PaletteUvModeCost = new int[PaletteUvModeContexts * 2];
    public readonly int[] PaletteYColorCost = new int[PaletteSizes * PaletteColorIndexContexts * PaletteColors];
    public readonly int[] PaletteUvColorCost = new int[PaletteSizes * PaletteColorIndexContexts * PaletteColors];
    public readonly int[] CflCost = new int[CflJointSigns * 2 * CflAlphabetSize];                // [joint sign][plane][alpha]
    public readonly int[] TxSizeCost = new int[MaxTxCats * TxSizeContexts * 5];                  // [cat][ctx][TX_SIZES]
    public readonly int[] TxfmPartitionCost = new int[TxfmPartitionContexts * 2];
    public readonly int[] IntraTxTypeCosts = new int[ExtTxSetsIntra * ExtTxSizes * IntraModes * TxTypes];   // [set][sqr tx][mode][type]
    public readonly int[] InterTxTypeCosts = new int[ExtTxSetsInter * ExtTxSizes * TxTypes];                // [set][sqr tx][type]
    public readonly int[] AngleDeltaCost = new int[DirectionalModes * (2 * MaxAngleDelta + 1)];
    public readonly int[] IntrabcCost = new int[2];
    public readonly int[] SwitchableRestoreCost = new int[RestoreSwitchableTypes];
    public readonly int[] WienerRestoreCost = new int[2];
    public readonly int[] SgrprojRestoreCost = new int[2];
}

/// <summary>WinnerModeStats (av1/encoder/block.h).</summary>
internal sealed class AomWinnerModeStats
{
    public readonly AomMbModeInfo Mbmi = new();
    public AomRdStats RdCost;
    public long Rd;
    public int RateY, RateUv;
    public readonly byte[] ColorIndexMap = new byte[128 * 128];   // [MAX_SB_SQUARE]
    public int ModeIndex;
}

/// <summary>PICK_MODE_CONTEXT (av1/encoder/context_tree.h), the intra fields.</summary>
internal sealed class AomPickModeContext
{
    public readonly AomMbModeInfo Mic = new();
    public readonly byte[]?[] ColorIndexMap = new byte[]?[2];
    public byte[] TxTypeMap;
    public int NumFourByFourBlk;
    public readonly ushort[][] Eobs = new ushort[3][];
    public readonly byte[][] TxbEntropyCtx = new byte[3][];
    public int Skippable;
    public AomRdStats RdStats;
    public int RdModeIsReady;
    public readonly AomMbmiExtFrame MbmiExtBest = new();

    /// <summary>av1_alloc_pmc (the coefficient buffers are the shared per-plane ones in the MACROBLOCK).</summary>
    public AomPickModeContext(int bsize, bool allowScreenContentTools)
    {
        int numPix = BlockSizeWide[bsize] * BlockSizeHigh[bsize];
        int numBlk = numPix / 16;
        TxTypeMap = new byte[numBlk];
        NumFourByFourBlk = numBlk;
        for (int i = 0; i < 3; i++) { Eobs[i] = new ushort[numBlk]; TxbEntropyCtx[i] = new byte[numBlk]; }
        if (numPix <= 64 * 64 && allowScreenContentTools)
            for (int i = 0; i < 2; i++) ColorIndexMap[i] = new byte[numPix];
        RdStats.Invalidate();
    }

    /// <summary>av1_copy_tree_context.</summary>
    public void CopyFrom(AomPickModeContext src)
    {
        Mic.CopyFrom(src.Mic);
        MbmiExtBest.CopyFrom(src.MbmiExtBest);
        NumFourByFourBlk = src.NumFourByFourBlk;
        Skippable = src.Skippable;
        Array.Copy(src.TxTypeMap, TxTypeMap, src.NumFourByFourBlk);
        RdStats = src.RdStats;
        RdModeIsReady = src.RdModeIsReady;
    }
}

/// <summary>PC_TREE (av1/encoder/context_tree.h).</summary>
internal sealed class AomPcTree
{
    public int Partitioning = PARTITION_NONE;
    public int BlockSize;
    public AomPickModeContext? None;
    public readonly AomPickModeContext?[] Horizontal = new AomPickModeContext?[2], Vertical = new AomPickModeContext?[2];
    public readonly AomPickModeContext?[] HorizontalA = new AomPickModeContext?[3], HorizontalB = new AomPickModeContext?[3],
        VerticalA = new AomPickModeContext?[3], VerticalB = new AomPickModeContext?[3];
    public readonly AomPickModeContext?[] Horizontal4 = new AomPickModeContext?[4], Vertical4 = new AomPickModeContext?[4];
    public readonly AomPcTree?[] Split = new AomPcTree?[4];
    public int Index;

    /// <summary>av1_alloc_pc_tree_node.</summary>
    public AomPcTree(int bsize) { BlockSize = bsize; }

    /// <summary>av1_free_pc_tree_recursive (search types other than VAR_BASED_PARTITION).</summary>
    public static void FreeRecursive(AomPcTree? t, bool keepBest, bool keepNone)
    {
        if (t == null) return;
        int partition = t.Partitioning;
        if (!keepNone && (!keepBest || partition != PARTITION_NONE)) t.None = null;
        for (int i = 0; i < 2; ++i)
        {
            if (!keepBest || partition != PARTITION_HORZ) t.Horizontal[i] = null;
            if (!keepBest || partition != PARTITION_VERT) t.Vertical[i] = null;
        }
        for (int i = 0; i < 3; ++i)
        {
            if (!keepBest || partition != PARTITION_HORZ_A) t.HorizontalA[i] = null;
            if (!keepBest || partition != PARTITION_HORZ_B) t.HorizontalB[i] = null;
            if (!keepBest || partition != PARTITION_VERT_A) t.VerticalA[i] = null;
            if (!keepBest || partition != PARTITION_VERT_B) t.VerticalB[i] = null;
        }
        for (int i = 0; i < 4; ++i)
        {
            if (!keepBest || partition != PARTITION_HORZ_4) t.Horizontal4[i] = null;
            if (!keepBest || partition != PARTITION_VERT_4) t.Vertical4[i] = null;
        }
        if (!keepBest || partition != PARTITION_SPLIT)
            for (int i = 0; i < 4; ++i)
                if (t.Split[i] != null) { FreeRecursive(t.Split[i], false, false); t.Split[i] = null; }
    }
}

/// <summary>MACROBLOCK (av1/encoder/block.h): the encoder-side state of the current block (partial: the port's files
/// add the fields their functions use).</summary>
internal sealed partial class AomMacroblock
{
    public readonly AomMbPlane[] Plane = { new(), new(), new() };
    public readonly AomMacroblockD E = new();
    public int Qindex;
    public int DeltaQindex;
    public int Rdmult;
    public int IntraSbRdmultModifier = 128;
    public readonly AomModeCosts ModeCosts = new();
    public readonly AomCoeffCosts CoeffCosts = new();
    public int Errorperbit;
    public readonly AomTxfmSearchParams TxfmSearchParams = new();
    public AomWinnerModeStats[] WinnerModeStats = System.Array.Empty<AomWinnerModeStats>();
    public int WinnerModeCount;
    public int RdModel;                       // TXFM_RD_MODEL: FULL_TXFM_RD / LOW_TXFM_RD
    public int[] DqcoeffBuf = new int[AomMbPlane.MaxSbSquare];
    public uint SourceVariance;
    public bool MustFindValidPartition;
}
