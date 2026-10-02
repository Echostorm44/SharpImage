using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>The mode-info the deblocker reads, one entry per 4x4 (mi) unit of the frame: the minimal stand-in for
/// cm->mi_params.mi_grid_base. TxSize is the resolved luma transform size at that 4x4 (mbmi->tx_size for intra blocks,
/// the inter_tx_size entry for non-skipped inter blocks); chroma sizes derive from Bsize. Coded = false marks an
/// uncoded mi (a null grid pointer: that edge stops filtering, as in libaom).</summary>
internal sealed class AomLfMiGrid
{
    public readonly int MiRows, MiCols;
    public readonly bool[] Coded;
    public readonly byte[] Bsize, TxSize, Mode, SegmentId;
    public readonly bool[] Skip, IsInter;
    public readonly sbyte[] RefFrame0;
    /// <summary>xd->lossless[segment_id] (coded lossless segments filter as TX_4X4).</summary>
    public readonly bool[] LosslessSeg = new bool[8];

    public AomLfMiGrid(int miRows, int miCols)
    {
        MiRows = miRows;
        MiCols = miCols;
        int n = miRows * miCols;
        Coded = new bool[n];
        Bsize = new byte[n];
        TxSize = new byte[n];
        Mode = new byte[n];
        SegmentId = new byte[n];
        Skip = new bool[n];
        IsInter = new bool[n];
        RefFrame0 = new sbyte[n];
    }

    /// <summary>Marks the block at (miRow, miCol) of size bsize (clipped to the frame) with one intra block's info.</summary>
    public void SetIntraBlock(int miRow, int miCol, int bsize, int txSize, int mode, int segmentId = 0, bool skip = false)
    {
        int h = Math.Min(MiSizeHigh[bsize], MiRows - miRow), w = Math.Min(MiSizeWide[bsize], MiCols - miCol);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = (miRow + r) * MiCols + miCol + c;
                Coded[i] = true;
                Bsize[i] = (byte)bsize;
                TxSize[i] = (byte)txSize;
                Mode[i] = (byte)mode;
                SegmentId[i] = (byte)segmentId;
                Skip[i] = skip;
                IsInter[i] = false;
                RefFrame0[i] = 0;   // INTRA_FRAME
            }
    }
}

/// <summary>struct loopfilter: the frame's filter levels and deltas.</summary>
internal sealed class AomLoopFilterParams
{
    public const int MaxLoopFilter = 63;
    public readonly int[] FilterLevel = new int[2];
    public int FilterLevelU, FilterLevelV;
    public int SharpnessLevel;
    public bool ModeRefDeltaEnabled = true;
    // av1_set_default_ref_deltas / av1_set_default_mode_deltas
    public readonly sbyte[] RefDeltas = { 1, 0, 0, 0, -1, 0, -1, -1 };
    public readonly sbyte[] ModeDeltas = { 0, 0 };
    /// <summary>lf->backup_filter_level[0..1], _u, _v (the searched levels before the skip heuristics) and
    /// cm->cur_frame->filter_level[0..1] (-1 when the level was not searched).</summary>
    public readonly int[] BackupFilterLevel = new int[4];
    public readonly int[] FrameFilterLevel = { -1, -1 };

    public AomLoopFilterParams Clone()
    {
        var c = new AomLoopFilterParams
        {
            FilterLevelU = FilterLevelU, FilterLevelV = FilterLevelV, SharpnessLevel = SharpnessLevel,
            ModeRefDeltaEnabled = ModeRefDeltaEnabled,
        };
        c.FilterLevel[0] = FilterLevel[0];
        c.FilterLevel[1] = FilterLevel[1];
        RefDeltas.CopyTo(c.RefDeltas, 0);
        ModeDeltas.CopyTo(c.ModeDeltas, 0);
        return c;
    }
}

/// <summary>Port of libaom av1/common/av1_loopfilter.c + thread_common.c's single-threaded frame driver
/// (av1_loop_filter_frame_mt with one worker): the per-plane, per-SB-row vertical-then-horizontal edge walk, either
/// per edge with set_lpf_parameters (lpf_opt_level 0) or per line with set_lpf_parameters_for_line_* and the dual/quad
/// kernels (av1_filter_block_plane_*_opt, lpf_opt_level 1; 2 also filters U and V jointly), calling the vectorized
/// aom_lpf kernels (AomLpf.Horizontal / Vertical). Delta-LF and segmentation loop-filter features are not ported (the
/// all-intra encoder leaves them off).</summary>
internal sealed class AomLoopFilter
{
    private const int MaxMibSize = 32, MaxMibSizeLog2 = 5, MiSize = 4, MaxSegments = 8, RefFrames = 8, MaxModeLfDeltas = 2;

    // mode_lf_lut
    private static readonly byte[] ModeLfLut =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,   // INTRA_MODES
        1, 1, 0, 1,                              // INTER_MODES (GLOBALMV == 0)
        1, 1, 1, 1, 1, 1, 0, 1,                  // INTER_COMPOUND_MODES (GLOBAL_GLOBALMV == 0)
    };

    private static readonly int[] TxDimToFilterLength = { 4, 8, 14, 14, 14 };

    // loop_filter_info_n: lfthr[lvl] (mblim, lim, hev_thr) and lvl[plane][seg][dir][ref][mode]
    private readonly byte[] _mblim = new byte[MaxLf + 1], _lim = new byte[MaxLf + 1], _hevThr = new byte[MaxLf + 1];
    private readonly byte[] _lvl = new byte[3 * MaxSegments * 2 * RefFrames * MaxModeLfDeltas];
    private const int MaxLf = AomLoopFilterParams.MaxLoopFilter;
    private int _bitDepth = 8;

    public AomLoopFilter()
    {
        // av1_loop_filter_init: sharpness 0 limits and the hev thresholds
        UpdateSharpness(0);
        for (int lvl = 0; lvl <= MaxLf; lvl++) _hevThr[lvl] = (byte)(lvl >> 4);
    }

    private static int LvlIndex(int plane, int seg, int dir, int refFrame, int mode)
        => (((plane * MaxSegments + seg) * 2 + dir) * RefFrames + refFrame) * MaxModeLfDeltas + mode;

    /// <summary>update_sharpness.</summary>
    private void UpdateSharpness(int sharpnessLvl)
    {
        for (int lvl = 0; lvl <= MaxLf; lvl++)
        {
            int blockInsideLimit = lvl >> ((sharpnessLvl > 0 ? 1 : 0) + (sharpnessLvl > 4 ? 1 : 0));
            if (sharpnessLvl > 0 && blockInsideLimit > 9 - sharpnessLvl) blockInsideLimit = 9 - sharpnessLvl;
            if (blockInsideLimit < 1) blockInsideLimit = 1;
            _lim[lvl] = (byte)blockInsideLimit;
            _mblim[lvl] = (byte)(2 * (lvl + 2) + blockInsideLimit);
        }
    }

    /// <summary>av1_loop_filter_frame_init.</summary>
    private void FrameInit(AomLoopFilterParams lf, int planeStart, int planeEnd)
    {
        UpdateSharpness(lf.SharpnessLevel);
        Span<int> filtLvl = stackalloc int[] { lf.FilterLevel[0], lf.FilterLevelU, lf.FilterLevelV };
        Span<int> filtLvlR = stackalloc int[] { lf.FilterLevel[1], lf.FilterLevelU, lf.FilterLevelV };
        for (int plane = planeStart; plane < planeEnd; plane++)
        {
            if (plane == 0 && filtLvl[0] == 0 && filtLvlR[0] == 0) break;
            else if (plane == 1 && filtLvl[1] == 0) continue;
            else if (plane == 2 && filtLvl[2] == 0) continue;
            for (int segId = 0; segId < MaxSegments; segId++)
                for (int dir = 0; dir < 2; ++dir)
                {
                    int lvlSeg = dir == 0 ? filtLvl[plane] : filtLvlR[plane];
                    if (!lf.ModeRefDeltaEnabled)
                    {
                        _lvl.AsSpan(LvlIndex(plane, segId, dir, 0, 0), RefFrames * MaxModeLfDeltas).Fill((byte)lvlSeg);
                    }
                    else
                    {
                        int scale = 1 << (lvlSeg >> 5);
                        int intraLvl = lvlSeg + lf.RefDeltas[0] * scale;
                        _lvl[LvlIndex(plane, segId, dir, 0, 0)] = (byte)Math.Clamp(intraLvl, 0, MaxLf);
                        for (int rf = 1; rf < RefFrames; ++rf)
                            for (int mode = 0; mode < MaxModeLfDeltas; ++mode)
                            {
                                int interLvl = lvlSeg + lf.RefDeltas[rf] * scale + lf.ModeDeltas[mode] * scale;
                                _lvl[LvlIndex(plane, segId, dir, rf, mode)] = (byte)Math.Clamp(interLvl, 0, MaxLf);
                            }
                    }
                }
        }
    }

    private int GetFilterLevel(AomLfMiGrid mi, int idx, int dirIdx, int plane)
        => _lvl[LvlIndex(plane, mi.SegmentId[idx], dirIdx, Math.Max((int)mi.RefFrame0[idx], 0), ModeLfLut[mi.Mode[idx]])];

    /// <summary>get_transform_size.</summary>
    private static int GetTransformSize(AomLfMiGrid mi, int idx, int plane, int ssX, int ssY)
    {
        if (mi.LosslessSeg[mi.SegmentId[idx]]) return TX_4X4;
        return plane == 0 ? mi.TxSize[idx] : AomEncodeMb.MaxUvTxsize(mi.Bsize[idx], ssX, ssY);
    }

    /// <summary>set_lpf_parameters: returns the transform size (TX_INVALID when a needed mi is uncoded) and the filter
    /// length / level of the edge at plane position (x, y).</summary>
    private int SetLpfParameters(AomLfMiGrid mi, bool vert, int x, int y, int plane, int ssX, int ssY, int width,
        int height, out int filterLength, out int level)
    {
        filterLength = 0;
        level = 0;
        if (width <= x || height <= y) return TX_4X4;
        int miRow = ssY | ((y << ssY) >> 2);
        int miCol = ssX | ((x << ssX) >> 2);
        int idx = miRow * mi.MiCols + miCol;
        if (!mi.Coded[idx]) return TX_INVALID;
        int ts = GetTransformSize(mi, idx, plane, ssX, ssY);
        int coord = vert ? x : y;
        int transformMasks = vert ? TxSizeWide[ts] - 1 : TxSizeHigh[ts] - 1;
        if ((coord & transformMasks) != 0) return ts;   // not a TU edge

        int dirIdx = vert ? 0 : 1;
        int currLevel = GetFilterLevel(mi, idx, dirIdx, plane);
        bool currSkipped = mi.Skip[idx] && mi.IsInter[idx];
        int lvl = currLevel;
        if (coord != 0)
        {
            int prevIdx = vert ? idx - (1 << ssX) : idx - (mi.MiCols << ssY);
            if (!mi.Coded[prevIdx]) return TX_INVALID;
            int pvRow = vert ? miRow : miRow - (1 << ssY);
            int pvCol = vert ? miCol - (1 << ssX) : miCol;
            _ = pvRow; _ = pvCol;
            int pvTs = GetTransformSize(mi, prevIdx, plane, ssX, ssY);
            int pvLvl = GetFilterLevel(mi, prevIdx, dirIdx, plane);
            bool pvSkip = mi.Skip[prevIdx] && mi.IsInter[prevIdx];
            int bsize = AomEncodeMb.PlaneBlockSize(mi.Bsize[idx], ssX, ssY);
            int predictionMasks = vert ? BlockSizeWide[bsize] - 1 : BlockSizeHigh[bsize] - 1;
            bool puEdge = (coord & predictionMasks) == 0;
            if ((currLevel != 0 || pvLvl != 0) && (!pvSkip || !currSkipped || puEdge))
            {
                int dim = vert
                    ? Math.Min(TxSizeWideUnitLog2[ts], TxSizeWideUnitLog2[pvTs])
                    : Math.Min(TxSizeHighUnitLog2[ts], TxSizeHighUnitLog2[pvTs]);
                filterLength = plane != 0 ? (dim == 0 ? 4 : 6) : TxDimToFilterLength[dim];
                // update the level if the current block is skipped, but the previous one is not
                lvl = currLevel != 0 ? currLevel : pvLvl;
            }
        }
        level = lvl;
        return ts;
    }

    /// <summary>av1_filter_block_plane_vert for the 128x128 (MAX_MIB_SIZE) unit at (miRow, miCol).</summary>
    private void FilterBlockPlaneVert(AomYv12Plane pl, AomLfMiGrid mi, int plane, int ssX, int ssY, int miRow, int miCol)
    {
        int planeMiRows = (mi.MiRows + ((1 << ssY) >> 1)) >> ssY;
        int planeMiCols = (mi.MiCols + ((1 << ssX) >> 1)) >> ssX;
        int yRange = Math.Min(planeMiRows - (miRow >> ssY), MaxMibSize >> ssY);
        int xRange = Math.Min(planeMiCols - (miCol >> ssX), MaxMibSize >> ssX);
        int x0 = (miCol * MiSize) >> ssX, y0 = (miRow * MiSize) >> ssY;
        for (int y = 0; y < yRange; y++)
            for (int x = 0; x < xRange;)
            {
                int currX = x0 + x * MiSize, currY = y0 + y * MiSize;
                int txSize = SetLpfParameters(mi, true, currX, currY, plane, ssX, ssY, pl.CropWidth, pl.CropHeight,
                    out int len, out int lvl);
                if (txSize == TX_INVALID)
                {
                    len = 0;
                    txSize = TX_4X4;
                }
                if (len != 0) Filter(true, len, pl, pl.At(currX, currY), pl.Stride, 4, lvl);
                x += TxSizeWideUnit[txSize];
            }
    }

    /// <summary>av1_filter_block_plane_horz.</summary>
    private void FilterBlockPlaneHorz(AomYv12Plane pl, AomLfMiGrid mi, int plane, int ssX, int ssY, int miRow, int miCol)
    {
        int planeMiRows = (mi.MiRows + ((1 << ssY) >> 1)) >> ssY;
        int planeMiCols = (mi.MiCols + ((1 << ssX) >> 1)) >> ssX;
        int yRange = Math.Min(planeMiRows - (miRow >> ssY), MaxMibSize >> ssY);
        int xRange = Math.Min(planeMiCols - (miCol >> ssX), MaxMibSize >> ssX);
        int x0 = (miCol * MiSize) >> ssX, y0 = (miRow * MiSize) >> ssY;
        for (int x = 0; x < xRange; x++)
            for (int y = 0; y < yRange;)
            {
                int currX = x0 + x * MiSize, currY = y0 + y * MiSize;
                int txSize = SetLpfParameters(mi, false, currX, currY, plane, ssX, ssY, pl.CropWidth, pl.CropHeight,
                    out int len, out int lvl);
                if (txSize == TX_INVALID)
                {
                    len = 0;
                    txSize = TX_4X4;
                }
                if (len != 0) Filter(false, len, pl, pl.At(currX, currY), pl.Stride, 4, lvl);
                y += TxSizeHighUnit[txSize];
            }
    }

    /// <summary>filter_vert / filter_horz: the dispatched aom_lpf_{vertical,horizontal}_{len} kernel over lines = 4
    /// (single), 8 (_dual) or 16 (_quad) lines with the level's thresholds.</summary>
    private void Filter(bool vert, int len, AomYv12Plane pl, int s, int stride, int lines, int lvl)
    {
        if (pl.Buf16 != null)
        {
            // the highbd kernels (single / _dual) filter line by line like the C: 4 lines per call
            int bl16 = _mblim[lvl], li16 = _lim[lvl], th16 = _hevThr[lvl];
            for (int k = 0; k < lines; k += 4)
            {
                if (vert) AomLpf.ApplyHighbd(len, pl.Buf16, s + k * stride, 1, stride, bl16, li16, th16, _bitDepth);
                else AomLpf.ApplyHighbd(len, pl.Buf16, s + k, stride, 1, bl16, li16, th16, _bitDepth);
            }
            return;
        }
        Filter(vert, len, pl.Buf, s, stride, lines, lvl);
    }

    private void Filter(bool vert, int len, byte[] buf, int s, int stride, int lines, int lvl)
    {
        int bl = _mblim[lvl], li = _lim[lvl], th = _hevThr[lvl];
        if (AomLpf.SimdSupported)
        {
            if (vert) AomLpf.Vertical(len, buf, s, stride, lines, bl, li, th);
            else AomLpf.Horizontal(len, buf, s, stride, lines, bl, li, th);
            return;
        }
        for (int k = 0; k < lines; k += 4)
        {
            if (vert) AomLpf.Apply(len, buf, s + k * stride, 1, stride, bl, li, th);
            else AomLpf.Apply(len, buf, s + k, stride, 1, bl, li, th);
        }
    }

    // ---- the lpf_opt_level 1 / 2 paths (av1_filter_block_plane_{vert,horz}_opt{,_chroma}) ----------------------------

    // vert_filter_length_luma[TX_SIZES_ALL][TX_SIZES_ALL]
    private static readonly byte[] VertFilterLengthLuma =
    {
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8,
        4, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
        4, 8, 14, 14, 14, 4, 8, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14, 14,
    };

    // horz_filter_length_luma[TX_SIZES_ALL][TX_SIZES_ALL]
    private static readonly byte[] HorzFilterLengthLuma =
    {
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8, 8, 8, 8, 4, 8, 8, 8, 8,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
        4, 8, 14, 14, 14, 8, 4, 14, 8, 14, 14, 14, 14, 14, 4, 14, 8, 14, 14,
    };

    // vert_filter_length_chroma[TX_SIZES_ALL][TX_SIZES_ALL]
    private static readonly byte[] VertFilterLengthChroma =
    {
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6,
    };

    // horz_filter_length_chroma[TX_SIZES_ALL][TX_SIZES_ALL]
    private static readonly byte[] HorzFilterLengthChroma =
    {
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
        4, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6, 6, 6, 6, 4, 6, 6, 6, 6,
    };

    // AV1_DEBLOCKING_PARAMETERS params_buf[MAX_MIB_SIZE] / TX_SIZE tx_buf[MAX_MIB_SIZE]
    private readonly byte[] _pLen = new byte[MaxMibSize], _pLvl = new byte[MaxMibSize], _txBuf = new byte[MaxMibSize];

    /// <summary>mi_prev != mbmi for the aligned blocks of a coded frame: the step back crosses the block's leading edge.</summary>
    private static bool PuEdge(AomLfMiGrid mi, int idx, bool vert, int c, int step)
    {
        int dim = vert ? MiSizeWide[mi.Bsize[idx]] : MiSizeHigh[mi.Bsize[idx]];
        return (c & (dim - 1)) < step;
    }

    /// <summary>set_one_param_for_line_luma: params[p] and the transform size at (miRow, miCol).</summary>
    private int SetOneParamLuma(AomLfMiGrid mi, bool vert, int miCol, int miRow, int coord, bool isFirstBlock,
        int prevTxSize, int modeStep, ref int minDim, int p)
    {
        _pLen[p] = 0;
        int idx = miRow * mi.MiCols + miCol;
        int ts = GetTransformSize(mi, idx, 0, 0, 0);
        if (!isFirstBlock || coord != 0)
        {
            int prevIdx = idx - modeStep;
            int pvTs = isFirstBlock ? GetTransformSize(mi, prevIdx, 0, 0, 0) : prevTxSize;
            if (isFirstBlock) minDim = vert ? BlockSizeHigh[mi.Bsize[prevIdx]] : BlockSizeWide[mi.Bsize[prevIdx]];
            int dirIdx = vert ? 0 : 1;
            int level = GetFilterLevel(mi, idx, dirIdx, 0);
            if (level == 0) level = GetFilterLevel(mi, prevIdx, dirIdx, 0);
            bool puEdge = PuEdge(mi, idx, vert, vert ? miCol : miRow, 1);
            bool currSkipped = !puEdge && mi.Skip[idx] && mi.IsInter[idx];
            if ((puEdge || !currSkipped) && level != 0)
            {
                _pLen[p] = vert ? VertFilterLengthLuma[ts * TX_SIZES_ALL + pvTs] : HorzFilterLengthLuma[ts * TX_SIZES_ALL + pvTs];
                _pLvl[p] = (byte)level;
            }
        }
        int blockDim = vert ? BlockSizeHigh[mi.Bsize[idx]] : BlockSizeWide[mi.Bsize[idx]];
        minDim = Math.Min(minDim, blockDim);
        return ts;
    }

    /// <summary>set_lpf_parameters_for_line_luma.</summary>
    private void SetLpfParametersForLineLuma(AomLfMiGrid mi, bool vert, int miCol, int miRow, int miRange, int modeStep,
        ref int minDim)
    {
        int prevTxSize = TX_INVALID, p = 0;
        int counter = vert ? miCol : miRow;
        bool first = true;
        do
        {
            int ts = SetOneParamLuma(mi, vert, vert ? counter : miCol, vert ? miRow : counter, counter, first, prevTxSize,
                modeStep, ref minDim, p);
            _txBuf[p] = (byte)ts;
            int advance = vert ? TxSizeWideUnit[ts] : TxSizeHighUnit[ts];
            prevTxSize = ts;
            counter += advance;
            p += advance;
            first = false;
        } while (counter < miRange);
    }

    /// <summary>set_one_param_for_line_chroma.</summary>
    private int SetOneParamChroma(AomLfMiGrid mi, bool vert, int miCol, int miRow, int coord, bool isFirstBlock,
        int prevTxSize, int modeStep, int ssX, int ssY, ref int minDim, int plane, int p)
    {
        _pLen[p] = 0;
        miRow |= ssY;
        miCol |= ssX;
        int idx = miRow * mi.MiCols + miCol;
        int ts = GetTransformSize(mi, idx, plane, ssX, ssY);
        if (!isFirstBlock || coord != 0)
        {
            int prevIdx = idx - modeStep;
            int pvTs = isFirstBlock ? GetTransformSize(mi, prevIdx, plane, ssX, ssY) : prevTxSize;
            if (isFirstBlock) minDim = vert ? TxSizeHigh[pvTs] : TxSizeWide[pvTs];
            int dirIdx = vert ? 0 : 1;
            int level = GetFilterLevel(mi, idx, dirIdx, plane);
            if (level == 0) level = GetFilterLevel(mi, prevIdx, dirIdx, plane);
            bool puEdge = PuEdge(mi, idx, vert, vert ? miCol : miRow, vert ? 1 << ssX : 1 << ssY);
            bool currSkipped = !puEdge && mi.Skip[idx] && mi.IsInter[idx];
            if ((!currSkipped || puEdge) && level != 0)
            {
                _pLen[p] = vert ? VertFilterLengthChroma[ts * TX_SIZES_ALL + pvTs] : HorzFilterLengthChroma[ts * TX_SIZES_ALL + pvTs];
                _pLvl[p] = (byte)level;
            }
        }
        int txDim = vert ? TxSizeHigh[ts] : TxSizeWide[ts];
        minDim = Math.Min(minDim, txDim);
        return ts;
    }

    /// <summary>set_lpf_parameters_for_line_chroma.</summary>
    private void SetLpfParametersForLineChroma(AomLfMiGrid mi, bool vert, int miCol, int miRow, int miRange, int modeStep,
        int ssX, int ssY, ref int minDim, int plane)
    {
        int prevTxSize = TX_INVALID, p = 0;
        int counter = vert ? miCol : miRow;
        int scale = vert ? ssX : ssY;
        bool first = true;
        do
        {
            int ts = SetOneParamChroma(mi, vert, vert ? counter : miCol, vert ? miRow : counter, counter, first,
                prevTxSize, modeStep, ssX, ssY, ref minDim, plane, p);
            _txBuf[p] = (byte)ts;
            int advance = vert ? TxSizeWideUnit[ts] : TxSizeHighUnit[ts];
            prevTxSize = ts;
            counter += advance << scale;
            p += advance;
            first = false;
        } while (counter < miRange);
    }

    /// <summary>av1_filter_block_plane_vert_opt (luma, num_mis_in_lpf_unit_height_log2 = MAX_MIB_SIZE_LOG2).</summary>
    private void FilterBlockPlaneVertOpt(AomYv12Plane pl, AomLfMiGrid mi, int miRow, int miCol)
    {
        int planeMiCols = (pl.CropWidth + MiSize - 1) >> 2, planeMiRows = (pl.CropHeight + MiSize - 1) >> 2;
        int yRange = Math.Min(planeMiRows - miRow, MaxMibSize), xRange = Math.Min(planeMiCols - miCol, MaxMibSize);
        for (int y = 0; y < yRange; y++)
        {
            int minBlockHeight = 128;
            SetLpfParametersForLineLuma(mi, true, miCol, miRow + y, miCol + xRange, 1, ref minBlockHeight);
            int s = pl.At(miCol * MiSize, (miRow + y) * MiSize);
            int lines = 4;
            if ((y & 3) == 0 && y + 3 < yRange && minBlockHeight >= 16)
            {
                lines = 16;
                y += 3;
            }
            else if (y + 1 < yRange && minBlockHeight >= 8)
            {
                lines = 8;
                y += 1;
            }
            for (int x = 0; x < xRange;)
            {
                int ts = _txBuf[x];
                if (ts == TX_INVALID)
                {
                    _pLen[x] = 0;
                    ts = TX_4X4;
                }
                if (_pLen[x] != 0) Filter(true, _pLen[x], pl, s + x * MiSize, pl.Stride, lines, _pLvl[x]);
                x += TxSizeWideUnit[ts];
            }
        }
    }

    /// <summary>av1_filter_block_plane_horz_opt (luma).</summary>
    private void FilterBlockPlaneHorzOpt(AomYv12Plane pl, AomLfMiGrid mi, int miRow, int miCol)
    {
        int planeMiCols = (pl.CropWidth + MiSize - 1) >> 2, planeMiRows = (pl.CropHeight + MiSize - 1) >> 2;
        int yRange = Math.Min(planeMiRows - miRow, MaxMibSize), xRange = Math.Min(planeMiCols - miCol, MaxMibSize);
        for (int x = 0; x < xRange; x++)
        {
            int minBlockWidth = 128;
            SetLpfParametersForLineLuma(mi, false, miCol + x, miRow, miRow + yRange, mi.MiCols, ref minBlockWidth);
            int s = pl.At((miCol + x) * MiSize, miRow * MiSize);
            int lines = 4;
            if ((x & 3) == 0 && x + 3 < xRange && minBlockWidth >= 16)
            {
                lines = 16;
                x += 3;
            }
            else if (x + 1 < xRange && minBlockWidth >= 8)
            {
                lines = 8;
                x += 1;
            }
            for (int y = 0; y < yRange;)
            {
                int ts = _txBuf[y];
                if (ts == TX_INVALID)
                {
                    _pLen[y] = 0;
                    ts = TX_4X4;
                }
                if (_pLen[y] != 0) Filter(false, _pLen[y], pl, s + y * MiSize * pl.Stride, pl.Stride, lines, _pLvl[y]);
                y += TxSizeHighUnit[ts];
            }
        }
    }

    /// <summary>av1_filter_block_plane_vert_opt_chroma; joint (lpf_opt_level 2) also filters plV with plane U's
    /// parameters.</summary>
    private void FilterBlockPlaneVertOptChroma(AomYv12Plane pl, AomYv12Plane? plV, AomLfMiGrid mi, int plane, int ssX,
        int ssY, int miRow, int miCol)
    {
        int miCols = ((pl.CropWidth << ssX) + MiSize - 1) >> 2, miRows = ((pl.CropHeight << ssY) + MiSize - 1) >> 2;
        int planeMiRows = (miRows + ((1 << ssY) >> 1)) >> ssY, planeMiCols = (miCols + ((1 << ssX) >> 1)) >> ssX;
        int yRange = Math.Min(planeMiRows - (miRow >> ssY), MaxMibSize >> ssY);
        int xRange = Math.Min(planeMiCols - (miCol >> ssX), MaxMibSize >> ssX);
        int modeStep = 1 << ssX;
        int s0 = pl.At((miCol * MiSize) >> ssX, (miRow * MiSize) >> ssY);
        for (int y = 0; y < yRange; y++)
        {
            int minHeight = 64;
            SetLpfParametersForLineChroma(mi, true, miCol, miRow + (y << ssY), miCol + (xRange << ssX), modeStep, ssX,
                ssY, ref minHeight, plane);
            int lines = 4, yInc = 0;
            if ((y & 3) == 0 && y + 3 < yRange && minHeight >= 16)
            {
                lines = 16;
                yInc = 3;
            }
            else if (y % 2 == 0 && y + 1 < yRange && minHeight >= 8)
            {
                lines = 8;
                yInc = 1;
            }
            for (int x = 0; x < xRange;)
            {
                int ts = _txBuf[x];
                if (ts == TX_INVALID)
                {
                    _pLen[x] = 0;
                    ts = TX_4X4;
                }
                int off = s0 + y * MiSize * pl.Stride + x * MiSize;
                if (_pLen[x] != 0)
                {
                    Filter(true, _pLen[x], pl, off, pl.Stride, lines, _pLvl[x]);
                    if (plV != null) Filter(true, _pLen[x], plV, off, plV.Stride, lines, _pLvl[x]);
                }
                x += TxSizeWideUnit[ts];
            }
            y += yInc;
        }
    }

    /// <summary>av1_filter_block_plane_horz_opt_chroma.</summary>
    private void FilterBlockPlaneHorzOptChroma(AomYv12Plane pl, AomYv12Plane? plV, AomLfMiGrid mi, int plane, int ssX,
        int ssY, int miRow, int miCol)
    {
        int miCols = ((pl.CropWidth << ssX) + MiSize - 1) >> 2, miRows = ((pl.CropHeight << ssY) + MiSize - 1) >> 2;
        int planeMiRows = (miRows + ((1 << ssY) >> 1)) >> ssY, planeMiCols = (miCols + ((1 << ssX) >> 1)) >> ssX;
        int yRange = Math.Min(planeMiRows - (miRow >> ssY), MaxMibSize >> ssY);
        int xRange = Math.Min(planeMiCols - (miCol >> ssX), MaxMibSize >> ssX);
        int modeStep = mi.MiCols << ssY;
        int s0 = pl.At((miCol * MiSize) >> ssX, (miRow * MiSize) >> ssY);
        for (int x = 0; x < xRange; x++)
        {
            int minWidth = 64;
            SetLpfParametersForLineChroma(mi, false, miCol + (x << ssX), miRow, miRow + (yRange << ssY), modeStep, ssX,
                ssY, ref minWidth, plane);
            int lines = 4, xInc = 0;
            if ((x & 3) == 0 && x + 3 < xRange && minWidth >= 16)
            {
                lines = 16;
                xInc = 3;
            }
            else if (x % 2 == 0 && x + 1 < xRange && minWidth >= 8)
            {
                lines = 8;
                xInc = 1;
            }
            for (int y = 0; y < yRange;)
            {
                int ts = _txBuf[y];
                if (ts == TX_INVALID)
                {
                    _pLen[y] = 0;
                    ts = TX_4X4;
                }
                int off = s0 + y * MiSize * pl.Stride + x * MiSize;
                if (_pLen[y] != 0)
                {
                    Filter(false, _pLen[y], pl, off, pl.Stride, lines, _pLvl[y]);
                    if (plV != null) Filter(false, _pLen[y], plV, off, plV.Stride, lines, _pLvl[y]);
                }
                y += TxSizeHighUnit[ts];
            }
            x += xInc;
        }
    }

    /// <summary>av1_loop_filter_frame_mt (one worker) on planes [planeStart, planeEnd): partialFrame filters only the
    /// middle rows (LPF_PICK_FROM_SUBIMAGE). lpfOptLevel: 0 the per-edge walk, 1 the dual/quad line walk, 2 also
    /// U and V jointly with U's parameters (libaom's lpf_opt_level).</summary>
    public void FilterFrame(AomYv12 frame, AomLfMiGrid mi, AomLoopFilterParams lf, int planeStart, int planeEnd,
        bool partialFrame = false, int lpfOptLevel = 0)
    {
        planeEnd = Math.Min(planeEnd, frame.NumPlanes);
        _bitDepth = frame.BitDepth;
        // check_planes_to_loop_filter
        Span<bool> planesToLf = stackalloc bool[3];
        planesToLf[0] = (lf.FilterLevel[0] != 0 || lf.FilterLevel[1] != 0) && planeStart <= 0 && 0 < planeEnd;
        planesToLf[1] = lf.FilterLevelU != 0 && planeStart <= 1 && 1 < planeEnd;
        planesToLf[2] = lf.FilterLevelV != 0 && planeStart <= 2 && 2 < planeEnd;
        if (!planesToLf[0] && planeStart <= 0 && 0 < planeEnd) return;
        if (!planesToLf[0] && !planesToLf[1] && !planesToLf[2]) return;

        int startMiRow = 0, miRowsToFilter = mi.MiRows;
        if (partialFrame && mi.MiRows > 8)
        {
            startMiRow = (mi.MiRows >> 1) & ~7;
            miRowsToFilter = Math.Max(mi.MiRows / 8, 8);
        }
        int endMiRow = startMiRow + miRowsToFilter;
        FrameInit(lf, planeStart, planeEnd);

        // loop_filter_rows
        for (int miRow = startMiRow; miRow < endMiRow; miRow += MaxMibSize)
            for (int plane = 0; plane < 3; ++plane)
            {
                // skip_loop_filter_plane
                bool joint = lpfOptLevel == 2 && plane == 1;
                if (lpfOptLevel == 2 && plane == 2) continue;
                if (joint ? !planesToLf[1] && !planesToLf[2] : !planesToLf[plane]) continue;
                var pl = frame.Planes[plane];
                var plV = joint && frame.NumPlanes > 2 ? frame.Planes[2] : null;
                int ssX = plane > 0 ? frame.SsX : 0, ssY = plane > 0 ? frame.SsY : 0;
                for (int miCol = 0; miCol < mi.MiCols; miCol += MaxMibSize)
                {
                    if (lpfOptLevel == 0) FilterBlockPlaneVert(pl, mi, plane, ssX, ssY, miRow, miCol);
                    else if (plane == 0) FilterBlockPlaneVertOpt(pl, mi, miRow, miCol);
                    else FilterBlockPlaneVertOptChroma(pl, plV, mi, plane, ssX, ssY, miRow, miCol);
                }
                for (int miCol = 0; miCol < mi.MiCols; miCol += MaxMibSize)
                {
                    if (lpfOptLevel == 0) FilterBlockPlaneHorz(pl, mi, plane, ssX, ssY, miRow, miCol);
                    else if (plane == 0) FilterBlockPlaneHorzOpt(pl, mi, miRow, miCol);
                    else FilterBlockPlaneHorzOptChroma(pl, plV, mi, plane, ssX, ssY, miRow, miCol);
                }
            }
    }
}
