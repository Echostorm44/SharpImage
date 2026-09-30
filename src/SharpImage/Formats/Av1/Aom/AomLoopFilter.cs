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
/// (av1_loop_filter_frame_mt with one worker): the per-plane, per-SB-row vertical-then-horizontal edge walk with
/// set_lpf_parameters (the lpf_opt_level = 0 path; the dual/quad "opt" paths libaom takes at lpf_opt_level 1/2 produce
/// the same frame, twin-verified). Delta-LF and segmentation loop-filter features are not ported (the all-intra
/// encoder leaves them off).</summary>
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
                if (len != 0) AomLpf.Apply(len, pl.Buf, pl.At(currX, currY), 1, pl.Stride, _mblim[lvl], _lim[lvl], _hevThr[lvl]);
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
                if (len != 0) AomLpf.Apply(len, pl.Buf, pl.At(currX, currY), pl.Stride, 1, _mblim[lvl], _lim[lvl], _hevThr[lvl]);
                y += TxSizeHighUnit[txSize];
            }
    }

    /// <summary>av1_loop_filter_frame_mt (one worker) on planes [planeStart, planeEnd): partialFrame filters only the
    /// middle rows (LPF_PICK_FROM_SUBIMAGE).</summary>
    public void FilterFrame(AomYv12 frame, AomLfMiGrid mi, AomLoopFilterParams lf, int planeStart, int planeEnd,
        bool partialFrame = false)
    {
        planeEnd = Math.Min(planeEnd, frame.NumPlanes);
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
                if (!planesToLf[plane]) continue;
                var pl = frame.Planes[plane];
                int ssX = plane > 0 ? frame.SsX : 0, ssY = plane > 0 ? frame.SsY : 0;
                for (int miCol = 0; miCol < mi.MiCols; miCol += MaxMibSize)
                    FilterBlockPlaneVert(pl, mi, plane, ssX, ssY, miRow, miCol);
                for (int miCol = 0; miCol < mi.MiCols; miCol += MaxMibSize)
                    FilterBlockPlaneHorz(pl, mi, plane, ssX, ssY, miRow, miCol);
            }
    }
}
