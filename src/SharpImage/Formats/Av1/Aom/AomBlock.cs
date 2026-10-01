using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

// libaom 3.14.1's encoder block state (av1/common/blockd.h, av1/encoder/block.h, av1/encoder/context_tree.h), mirrored
// for the port: C pointers become (array, offset) pairs; names follow libaom's so each ported function reads like its C.

/// <summary>RD_STATS (av1/encoder/rd.h).</summary>
internal struct AomRdStats
{
    public int Rate;
    public int ZeroRate;
    public long Dist;
    public long Rdcost;
    public long Sse;
    public byte SkipTxfm;

    /// <summary>av1_init_rd_stats.</summary>
    public void Init()
    {
        Rate = 0; Dist = 0; Rdcost = 0; Sse = 0; SkipTxfm = 1; ZeroRate = 0;
    }

    /// <summary>av1_invalid_rd_stats.</summary>
    public void Invalidate()
    {
        Rate = int.MaxValue; Dist = long.MaxValue; Rdcost = long.MaxValue; Sse = long.MaxValue; SkipTxfm = 0; ZeroRate = 0;
    }

    /// <summary>av1_merge_rd_stats.</summary>
    public void Merge(in AomRdStats src)
    {
        if (Rate == int.MaxValue || src.Rate == int.MaxValue) { Invalidate(); return; }
        Rate = (int)Math.Min((long)Rate + src.Rate, int.MaxValue);
        if (ZeroRate == 0) ZeroRate = src.ZeroRate;
        Dist += src.Dist;
        if (Sse < long.MaxValue && src.Sse < long.MaxValue) Sse += src.Sse;
        SkipTxfm &= src.SkipTxfm;
    }

    /// <summary>av1_rd_cost_update.</summary>
    public void CostUpdate(int rdmult)
    {
        if (Rate < int.MaxValue && Dist < long.MaxValue && Rdcost < long.MaxValue) Rdcost = AomRd.RdCost(rdmult, Rate, Dist);
        else Invalidate();
    }
}

/// <summary>PALETTE_MODE_INFO.</summary>
internal struct AomPaletteModeInfo
{
    public const int PaletteMaxSize = 8;
    public byte PaletteSize0, PaletteSize1;
    public ushort[] PaletteColors;   // [3 * PALETTE_MAX_SIZE]
}

/// <summary>MB_MODE_INFO (the intra key-frame fields).</summary>
internal sealed partial class AomMbModeInfo
{
    public int Bsize;
    public int Partition;
    public int Mode;       // PREDICTION_MODE
    public int UvMode;     // UV_PREDICTION_MODE
    public int CurrentQindex;
    public int RefFrame0, RefFrame1;
    public readonly sbyte[] AngleDelta = new sbyte[2];
    public byte UseFilterIntra, FilterIntraMode;
    public sbyte CflAlphaSigns;
    public byte CflAlphaIdx;
    public AomPaletteModeInfo Palette = new() { PaletteColors = new ushort[3 * AomPaletteModeInfo.PaletteMaxSize] };
    public byte SkipTxfm;
    public int TxSize;
    public readonly byte[] InterTxSize = new byte[16];   // INTER_TX_SIZE_BUF_LEN
    public byte SegmentId;
    public byte UseIntrabc;
    public byte SkipMode;
    public sbyte CdefStrength;
    // inter (intrabc) fields: mv[2], motion_mode, interp_filters (av1_broadcast_interp_filter packed: y << 16 | x)
    public AomMv Mv0, Mv1;
    public byte MotionMode;
    public uint InterpFilters;

    public void CopyFrom(AomMbModeInfo s)
    {
        Bsize = s.Bsize; Partition = s.Partition; Mode = s.Mode; UvMode = s.UvMode; CurrentQindex = s.CurrentQindex;
        RefFrame0 = s.RefFrame0; RefFrame1 = s.RefFrame1;
        AngleDelta[0] = s.AngleDelta[0]; AngleDelta[1] = s.AngleDelta[1];
        UseFilterIntra = s.UseFilterIntra; FilterIntraMode = s.FilterIntraMode;
        CflAlphaSigns = s.CflAlphaSigns; CflAlphaIdx = s.CflAlphaIdx;
        Palette.PaletteSize0 = s.Palette.PaletteSize0; Palette.PaletteSize1 = s.Palette.PaletteSize1;
        Array.Copy(s.Palette.PaletteColors, Palette.PaletteColors, Palette.PaletteColors.Length);
        SkipTxfm = s.SkipTxfm; TxSize = s.TxSize;
        Array.Copy(s.InterTxSize, InterTxSize, InterTxSize.Length);
        SegmentId = s.SegmentId; UseIntrabc = s.UseIntrabc; SkipMode = s.SkipMode; CdefStrength = s.CdefStrength;
        Mv0 = s.Mv0; Mv1 = s.Mv1; MotionMode = s.MotionMode; InterpFilters = s.InterpFilters;
    }

    public static readonly AomMbModeInfo Zero = new();

    public AomMbModeInfo Clone() { var m = new AomMbModeInfo(); m.CopyFrom(this); return m; }
}

/// <summary>A 2D sample buffer view (struct buf_2d): plane array, offset of the block's first sample, stride.</summary>
internal struct AomBuf2d
{
    public byte[] Buf;
    public int Offset, Stride, Width, Height;
}

/// <summary>struct macroblock_plane (MACROBLOCK_PLANE).</summary>
internal sealed partial class AomMbPlane
{
    public const int MaxSbSquare = 128 * 128;
    public readonly short[] SrcDiff = new short[MaxSbSquare];
    public int[] Coeff = new int[MaxSbSquare];
    public int[] Qcoeff = new int[MaxSbSquare];
    public int[] Dqcoeff = new int[MaxSbSquare];
    public ushort[] Eobs = new ushort[MaxSbSquare / 16];
    public byte[] TxbEntropyCtx = new byte[MaxSbSquare / 16];
    public AomBuf2d Src;
    // quantizer / dequantizer [dc, ac] for the block's qindex (x->plane[p].*_QTX)
    public short QuantFp0, QuantFp1, RoundFp0, RoundFp1, Quant0, Quant1, QuantShift0, QuantShift1,
        Zbin0, Zbin1, Round0, Round1, Dequant0, Dequant1;
}

/// <summary>struct macroblockd_plane (MACROBLOCKD_PLANE).</summary>
internal sealed partial class AomMbdPlane
{
    public int PlaneType;
    public int SubsamplingX, SubsamplingY;
    public AomBuf2d Dst;
    // above / left entropy contexts of the current block (arrays + offsets into the tile / SB buffers)
    public byte[] AboveEntropyContext = Array.Empty<byte>();
    public int AboveEntropyOffset;
    public byte[] LeftEntropyContext = Array.Empty<byte>();
    public int LeftEntropyOffset;
    public byte[] ColorIndexMap = new byte[AomMbPlane.MaxSbSquare];
    public int Width, Height;   // block width / height in 4x4 units (xd->plane[p].width / height)
}

/// <summary>TxfmSearchParams (av1/encoder/block.h).</summary>
internal sealed partial class AomTxfmSearchParams
{
    public int TxModeSearchType;          // TX_MODE
    public int TxSizeSearchMethod;        // TX_SIZE_SEARCH_METHOD
    public int SkipTxfmLevel;
    public int UseDefaultIntraTxType;
    public int UseDerivedIntraTxTypeSet;
    public int DefaultInterTxTypeProbThresh;
    public int Prune2dTxfmMode;
    public readonly uint[] CoeffOptThresholds = new uint[2];
    public int UseTransformDomainDistortion;
    public uint TxDomainDistThreshold;
    public int PredictDcLevel;
    public bool EnableNnPruneIntraTxDepths;
    public int NnPruneDepthsForIntraTx;   // TX_PRUNE_NONE / LARGEST / SPLIT
    public int UseQmDistMetric;
    public int ModeEvalType = -1;           // MODE_EVAL_TYPE of the last set_mode_eval_params
}
