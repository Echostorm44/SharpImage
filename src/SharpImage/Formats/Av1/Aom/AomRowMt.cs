using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    /// <summary>cpi->mt_info.num_workers: the encoder's worker count (0 / 1: no multi-threading).</summary>
    public int NumWorkers;
    /// <summary>The row-MT state while mt_info->row_mt_enabled (null: the single-threaded path, whose sync_read_ptr /
    /// sync_write_ptr are the dummies).</summary>
    public AomRowMt? RowMt;
    /// <summary>cpi->tile_data: the frame's tiles in raster order (av1_init_tile_data).</summary>
    public AomTileDataEnc[] TileData = Array.Empty<AomTileDataEnc>();
}

internal sealed partial class AomMacroblock
{
    /// <summary>The palette color map token list the final encode appends to (the current SB row's tplist entry).</summary>
    public List<AomPaletteToken> PaletteTokens = null!;
    // td->vt64x64 (the variance tree storage of the thread, reused by every superblock)
    public readonly AomVarTree VarTree = new();
    // td->pc_root of the non-RD path (allocated once per frame and thread and reused by every superblock)
    public AomPcTree? NonrdPcRoot;
    /// <summary>The tile the thread is encoding (its TileDataEnc: tile_info, row-MT sync, row_ctx).</summary>
    public AomTileDataEnc TileData = null!;
}

/// <summary>TileDataEnc (av1/encoder/encoder.h) for the all-intra frame: the tile, its CDFs (tctx: the tile's adaptive
/// contexts on the single-threaded path, the row-MT rows' starting point), allow_update_cdf, and the tile's row-MT
/// state (AV1EncRowMultiThreadSync, next_mi_row / num_threads_working, row_ctx) and palette token lists (tplist).</summary>
internal sealed partial class AomTileDataEnc
{
    public readonly AomTileInfo Tile;
    public readonly Av1CdfContext Tctx = new();
    public bool AllowUpdateCdf;
    // AV1EncRowMultiThreadSync
    internal int[] NumFinishedCols = Array.Empty<int>();
    internal object[] Mutex = Array.Empty<object>();
    internal int IntrabcExtraTopRightSbDelay;
    internal int NextMiRow, NumThreadsWorking;
    // this_tile->row_ctx: max(1, sb_cols_in_tile - 1) frame contexts
    internal AomRowMt.CdfArrays[] RowCtx = Array.Empty<AomRowMt.CdfArrays>();
    // the SB rows' palette token lists (tplist)
    internal List<AomPaletteToken>[] RowTokens = Array.Empty<List<AomPaletteToken>>();

    public AomTileDataEnc(AomTileInfo tile) { Tile = tile; }

    /// <summary>av1_init_tile_data for every tile of the frame (raster order): the tile, allow_update_cdf and
    /// tctx = cm->fc.</summary>
    internal static AomTileDataEnc[] InitAll(AomComp cpi)
    {
        var cm = cpi.Cm;
        var tiles = new AomTileDataEnc[cm.TileRows * cm.TileCols];
        for (int tileRow = 0; tileRow < cm.TileRows; ++tileRow)
            for (int tileCol = 0; tileCol < cm.TileCols; ++tileCol)
            {
                var td = new AomTileDataEnc(cm.TileInit(tileRow, tileCol));
                // allow_update_cdf = !large_scale && !disable_cdf_update && !delay_wait_for_top_right_sb
                td.AllowUpdateCdf = cpi.AllowUpdateCdf;
                td.Tctx.CopyFrom(cm.Fc);
                tiles[tileRow * cm.TileCols + tileCol] = td;
            }
        return tiles;
    }
}

/// <summary>Port of libaom 3.14.1's row-based multi-threading of the encode stage (ethread.c av1_encode_tiles_row_mt /
/// enc_row_mt_worker_hook / get_next_job / switch_tile_and_get_next_job / av1_row_mt_sync_read / av1_row_mt_sync_write,
/// encodeframe.c's row-MT parts of encode_sb_row, partition_search.c wait_for_top_right_sb, encodeframe_utils.c
/// av1_avg_cdf_symbols). Superblock rows of every tile run on worker threads in a wavefront per tile: row r's
/// superblock c starts once row r - 1 of the tile has finished superblock c + 1 - delay (+ the intrabc extra delay);
/// with CDF adaptation each row starts from the CDFs row r - 1 had after its second superblock and averages in the
/// top-right superblock's CDFs before every later superblock. A worker takes the next row of its tile and switches to
/// the tile with the fewest workers (then the most rows left) when its tile has none.</summary>
internal sealed class AomRowMt
{
    private const int AVG_CDF_WEIGHT_LEFT = 3, AVG_CDF_WEIGHT_TOP_RIGHT = 1;
    private const int SyncRange = 1;

    // AV1EncRowMultiThreadInfo
    private readonly object _jobMutex = new();
    private volatile bool _rowMtExit;
    private readonly int _delay;
    private readonly AomTileDataEnc[] _tiles;

    private AomRowMt(AomComp cpi)
    {
        var cm = cpi.Cm;
        _tiles = cpi.TileData;
        _delay = AomEncoder.DelayWaitForTopRightSb(cpi) ? 1 : 0;
        // av1_get_intrabc_extra_top_right_sb_delay
        int intrabcExtra = cpi.AllowIntrabcNow ? (cm.SbSize == BLOCK_128X128 ? 2 : 4) : 0;
        foreach (var t in _tiles)
        {
            int sbRows = t.Tile.SbRows(cm), sbCols = t.Tile.SbCols(cm);
            t.NumFinishedCols = new int[sbRows];
            t.Mutex = new object[sbRows];
            for (int i = 0; i < sbRows; i++) { t.NumFinishedCols[i] = -1; t.Mutex[i] = new object(); }
            t.IntrabcExtraTopRightSbDelay = intrabcExtra;
            t.NextMiRow = t.Tile.MiRowStart;
            t.NumThreadsWorking = 0;
            if (t.AllowUpdateCdf)
            {
                t.RowCtx = new CdfArrays[Math.Max(1, sbCols - 1)];
                for (int i = 0; i < t.RowCtx.Length; i++) t.RowCtx[i] = new CdfArrays(new Av1CdfContext());
            }
            t.RowTokens = new List<AomPaletteToken>[sbRows];
            for (int i = 0; i < sbRows; i++) t.RowTokens[i] = new List<AomPaletteToken>();
        }
    }

    /// <summary>compute_num_enc_row_mt_workers: the sum over the tiles of min((sb_cols + 1) / 2, sb_rows), at most
    /// max_threads.</summary>
    private static int ComputeNumEncRowMtWorkers(AomCommon cm, int maxThreads)
    {
        int total = 0;
        for (int row = 0; row < cm.TileRows; row++)
            for (int col = 0; col < cm.TileCols; col++)
            {
                var t = cm.TileInit(row, col);
                total += Math.Min((t.SbCols(cm) + 1) >> 1, t.SbRows(cm));
            }
        return Math.Min(maxThreads, total);
    }

    /// <summary>cpi->mt_info.num_workers for an all-intra frame: av1_get_max_num_workers over the modules'
    /// worker counts (av1_compute_num_workers_for_mt: one pass, row_mt 1), 0 when g_threads is 1.</summary>
    internal static int ComputeNumWorkers(AomCommon cm, int maxThreads)
    {
        if (maxThreads <= 1) return 0;
        int enc = ComputeNumEncRowMtWorkers(cm, maxThreads);               // MOD_FP / TF / TPL / ENC / LPF / CDEF / LR
        int ai = Math.Min(cm.MiRows / MiSizeWide[BLOCK_8X8], maxThreads);   // MOD_AI (compute_num_ai_workers)
        int packBs = Math.Min(maxThreads, cm.TileCols * cm.TileRows);      // MOD_PACK_BS (compute_num_enc_tile_mt_workers)
        int max = Math.Max(Math.Max(Math.Max(enc, ai), packBs), 1);        // MOD_GME: 1
        max = Math.Min(max, maxThreads);
        return max > 1 ? max : 0;   // no workers are created for a single one
    }

    /// <summary>av1_row_mt_sync_read.</summary>
    private static void SyncRead(AomTileDataEnc t, int r, int c)
    {
        if (r == 0) return;
        int nsync = SyncRange;
        int extra = t.IntrabcExtraTopRightSbDelay;
        if (c <= Volatile.Read(ref t.NumFinishedCols[r - 1]) - nsync - extra) return;
        var mutex = t.Mutex[r - 1];
        lock (mutex)
        {
            while (c > t.NumFinishedCols[r - 1] - nsync - extra) Monitor.Wait(mutex);
        }
    }

    /// <summary>av1_row_mt_sync_write.</summary>
    private static void SyncWrite(AomTileDataEnc t, int r, int c, int cols)
    {
        int nsync = SyncRange;
        int cur;
        bool sig = true;
        if (c < cols - 1)
        {
            cur = c;
            if (c % nsync != 0) sig = false;
        }
        else cur = cols + nsync + t.IntrabcExtraTopRightSbDelay;
        if (!sig) return;
        var mutex = t.Mutex[r];
        lock (mutex)
        {
            Volatile.Write(ref t.NumFinishedCols[r], Math.Max(t.NumFinishedCols[r], cur));
            Monitor.PulseAll(mutex);
        }
    }

    /// <summary>set_encoding_done (after an error): every row of every tile reports itself finished so no worker
    /// waits forever.</summary>
    private void SetEncodingDone(AomCommon cm)
    {
        foreach (var t in _tiles)
        {
            int sbRows = t.Tile.SbRows(cm), sbCols = t.Tile.SbCols(cm);
            for (int r = 0; r < sbRows; r++) SyncWrite(t, r, sbCols - 1, sbCols);
        }
    }

    /// <summary>wait_for_top_right_sb (rd_pick_sb_modes / pick_sb_modes_nonrd): with the top-only superblock wait
    /// (cost update frequency off / tile), the block on the superblock's top row that reaches its right edge waits for
    /// the top-right superblock.</summary>
    internal static void WaitForTopRightSb(AomComp cpi, AomMacroblock x, int bsize, int miRow, int miCol)
    {
        var mt = cpi.RowMt;
        if (mt == null) return;   // av1_row_mt_sync_read_dummy
        var cm = cpi.Cm;
        var xd = x.E;
        int sbSizeInMi = MiSizeWide[cm.SbSize];
        int bwInMi = MiSizeWide[bsize];
        int blkRowInSb = miRow & (sbSizeInMi - 1);
        int blkColInSb = miCol & (sbSizeInMi - 1);
        bool topRightBlockInSb = blkRowInSb == 0 && blkColInSb + bwInMi >= sbSizeInMi;
        if (!topRightBlockInSb) return;
        int sbRowInTile = (miRow - xd.TileMiRowStart) >> cm.MibSizeLog2;
        int sbColInTile = (miCol - xd.TileMiColStart) >> cm.MibSizeLog2;
        SyncRead(x.TileData, sbRowInTile, sbColInTile);
    }

    /// <summary>encode_sb_row's row-MT steps before a superblock: the top-right wait (sync_read_ptr) and, with CDF
    /// adaptation, the row's starting CDFs (restored at the first column) or the left / top-right average.
    /// Returns false when a worker has failed (row_mt_exit).</summary>
    internal bool BeforeSb(AomComp cpi, AomMacroblock x, int miRow, int miCol, int sbRow, int sbColInTile)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        var t = x.TileData;
        SyncRead(t, sbRow, sbColInTile - _delay);
        if (_rowMtExit) return false;
        if (t.AllowUpdateCdf && xd.TileMiRowStart != miRow)
        {
            var ctx = Arrays(x);
            if (xd.TileMiColStart == miCol)
                ctx.CopyFrom(t.RowCtx[0]);   // restore frame context at the 1st column sb
            else if (xd.TileMiColEnd > miCol + cm.MibSize)
                ctx.Average(t.RowCtx[sbColInTile], AVG_CDF_WEIGHT_LEFT, AVG_CDF_WEIGHT_TOP_RIGHT);
            else
                ctx.Average(t.RowCtx[sbColInTile - 1], AVG_CDF_WEIGHT_LEFT, AVG_CDF_WEIGHT_TOP_RIGHT);
        }
        return true;
    }

    /// <summary>encode_sb_row's row-MT steps after a superblock: save the top-right context for the next row and
    /// report the superblock finished (sync_write_ptr).</summary>
    internal void AfterSb(AomComp cpi, AomMacroblock x, int miRow, int sbRow, int sbColInTile, int sbColsInTile)
    {
        var cm = cpi.Cm;
        var t = x.TileData;
        if (t.AllowUpdateCdf && x.E.TileMiRowEnd > miRow + cm.MibSize)
        {
            if (sbColsInTile == 1) t.RowCtx[0].CopyFrom(Arrays(x));
            else if (sbColInTile >= 1) t.RowCtx[sbColInTile - 1].CopyFrom(Arrays(x));
        }
        SyncWrite(t, sbRow, sbColInTile, sbColsInTile);
    }

    // the flattened view of each thread's tile_ctx
    [ThreadStatic] private static CdfArrays? t_ctxArrays;
    private static CdfArrays Arrays(AomMacroblock x)
    {
        var a = t_ctxArrays;
        if (a == null || !ReferenceEquals(a.Ctx, x.TileCtx)) t_ctxArrays = a = new CdfArrays(x.TileCtx);
        return a;
    }

    /// <summary>av1_encode_tiles_row_mt: the tiles' row-MT state, the above contexts of every tile zeroed,
    /// assign_tile_to_thread, prepare_enc_workers (each worker's MACROBLOCK set up like the main one), launch the
    /// workers on the SB-row jobs, sync, and gather the tiles' rows' palette tokens in coding order.</summary>
    internal static void EncodeTilesRowMt(AomComp cpi, AomMacroblock mainX, Func<AomMacroblock> newThreadData, int maxThreads)
    {
        var cm = cpi.Cm;
        var mt = new AomRowMt(cpi);
        cpi.RowMt = mt;
        // num_mod_workers[MOD_ENC] (compute_num_enc_row_mt_workers), at most mt_info->num_workers
        int numWorkers = Math.Min(ComputeNumEncRowMtWorkers(cm, maxThreads), Math.Max(cpi.NumWorkers, 1));
        numWorkers = Math.Max(numWorkers, 1);
        foreach (var t in mt._tiles) cm.ZeroAboveContext(t.Tile.MiColStart, t.Tile.MiColEnd, t.Tile.TileRow);
        // assign_tile_to_thread
        int numTiles = mt._tiles.Length;
        var threadIdToTileId = new int[numWorkers];
        for (int i = 0, tileId = 0; i < numWorkers; i++)
        {
            threadIdToTileId[i] = tileId++;
            if (tileId == numTiles) tileId = 0;
        }

        Exception? error = null;
        using var done = new CountdownEvent(numWorkers);
        var workerData = new AomMacroblock?[numWorkers];
        void Run(AomMacroblock? x, int threadId)
        {
            try
            {
                x ??= newThreadData();
                workerData[threadId] = x;
                mt.WorkerHook(cpi, x, threadIdToTileId[threadId]);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, e, null);
                mt._rowMtExit = true;
                mt.SetEncodingDone(cm);
            }
            finally { done.Signal(); }
        }
        for (int i = numWorkers - 1; i >= 1; i--)
        {
            int id = i;
            ThreadPool.UnsafeQueueUserWorkItem(_ => Run(null, id), null);
        }
        Run(mainX, 0);   // the main thread is worker 0 (cpi->td)
        done.Wait();
        cpi.RowMt = null;
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        // accumulate_counters_enc_workers: the workers' counts into cpi->td (worker i > 0, highest first)
        for (int i = numWorkers - 1; i > 0; i--)
            if (workerData[i] is { } w) mainX.AccumulateWorker(w);

        // the token lists of the tiles' SB rows, in coding order
        cpi.PaletteTokens.Clear();
        foreach (var t in mt._tiles)
            foreach (var l in t.RowTokens) cpi.PaletteTokens.AddRange(l);
        mainX.PaletteTokens = cpi.PaletteTokens;
    }

    /// <summary>get_next_job.</summary>
    private static bool GetNextJob(AomTileDataEnc t, out int currentMiRow, int mibSize)
    {
        if (t.NextMiRow < t.Tile.MiRowEnd)
        {
            currentMiRow = t.NextMiRow;
            t.NumThreadsWorking++;
            t.NextMiRow += mibSize;
            return true;
        }
        currentMiRow = -1;
        return false;
    }

    /// <summary>switch_tile_and_get_next_job: the tile with jobs left and the fewest threads working (then the most
    /// rows left); false at the end of the frame.</summary>
    private bool SwitchTileAndGetNextJob(AomCommon cm, ref int curTileId, out int currentMiRow)
    {
        int tileId = -1, maxMisToEncode = 0, minNumThreadsWorking = int.MaxValue;
        for (int i = 0; i < _tiles.Length; i++)
        {
            var t = _tiles[i];
            int theoreticalLimitOnThreads = Math.Min((t.Tile.SbCols(cm) + 1) >> 1, t.Tile.SbRows(cm));
            int numThreadsWorking = t.NumThreadsWorking;
            if (numThreadsWorking < theoreticalLimitOnThreads)
            {
                int numMisToEncode = t.Tile.MiRowEnd - t.NextMiRow;
                if (numMisToEncode > 0)
                {
                    if (numThreadsWorking < minNumThreadsWorking)
                    {
                        minNumThreadsWorking = numThreadsWorking;
                        maxMisToEncode = 0;
                    }
                    if (numThreadsWorking == minNumThreadsWorking && numMisToEncode > maxMisToEncode)
                    {
                        tileId = i;
                        maxMisToEncode = numMisToEncode;
                    }
                }
            }
        }
        currentMiRow = -1;
        if (tileId == -1) return false;
        curTileId = tileId;
        GetNextJob(_tiles[tileId], out currentMiRow, cm.MibSize);
        return true;
    }

    /// <summary>enc_row_mt_worker_hook: take SB-row jobs (of the thread's tile, then of the least served tile) until
    /// the frame has none left.</summary>
    private void WorkerHook(AomComp cpi, AomMacroblock x, int curTileId)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        // td->tctx: the thread's frame context buffer
        var tctx = x.TileCtx;
        while (true)
        {
            int currentMiRow;
            lock (_jobMutex)
            {
                if (_rowMtExit) return;
                if (!GetNextJob(_tiles[curTileId], out currentMiRow, cm.MibSize) &&
                    !SwitchTileAndGetNextJob(cm, ref curTileId, out currentMiRow))
                    return;   // end_of_frame
            }
            var t = _tiles[curTileId];
            var tile = t.Tile;
            x.TileData = t;
            xd.SetTile(tile);
            int sbRow = (currentMiRow - tile.MiRowStart) >> cm.MibSizeLog2;
            x.TileCtx = tctx;
            if (t.AllowUpdateCdf)
            {
                if (currentMiRow == tile.MiRowStart) tctx.CopyFrom(t.Tctx);   // this_tile->tctx
            }
            else tctx.CopyFrom(t.Tctx);
            xd.InitAboveContext(cm, tile.TileRow);
            AomCfl.CflInit(xd.Cfl, cm.SsX, cm.SsY);
            x.PaletteTokens = t.RowTokens[sbRow];
            AomEncoder.EncodeSbRow(cpi, x, currentMiRow);
            lock (_jobMutex) t.NumThreadsWorking--;
        }
    }

    /// <summary>A frame context as its CDF arrays in a fixed order (the FRAME_CONTEXT struct copy and
    /// av1_avg_cdf_symbols, element by element: each array holds the inverse CDF values then the adaptation counter,
    /// libaom's layout without the constant terminating zero).</summary>
    internal sealed class CdfArrays
    {
        public readonly Av1CdfContext Ctx;
        private readonly ushort[][] _arrays;

        public CdfArrays(Av1CdfContext ctx)
        {
            Ctx = ctx;
            var list = new List<ushort[]>();
            ctx.CollectArrays(list);   // declaration order, no reflection (Native AOT trims / reorders field metadata)
            _arrays = list.ToArray();
        }

        public void CopyFrom(CdfArrays src)
        {
            var s = src._arrays;
            for (int i = 0; i < _arrays.Length; i++) s[i].AsSpan().CopyTo(_arrays[i]);
        }

        public void CopyFrom(Av1CdfContext src) => CopyFrom(new CdfArrays(src));

        /// <summary>av1_avg_cdf_symbols: this (the left context) = (left * wtLeft + tr * wtTr + (wtLeft + wtTr) / 2) /
        /// (wtLeft + wtTr) for every entry (entries equal in both are unchanged, so averaging the arrays libaom skips,
        /// the never-adapted ones, is the identity).</summary>
        public void Average(CdfArrays tr, int wtLeft, int wtTr)
        {
            var t = tr._arrays;
            int sum = wtLeft + wtTr, half = sum / 2;
            if (sum == 4)
            {
                // the weights libaom uses (3, 1): a shift instead of the division, and no compare (equal entries give
                // (4 l + 2) >> 2 = l)
                for (int i = 0; i < _arrays.Length; i++)
                {
                    var l = _arrays[i];
                    var r = t[i];
                    if (r.Length != l.Length) throw new InvalidOperationException("CDF layouts differ");
                    for (int j = 0; j < l.Length; j++) l[j] = (ushort)((l[j] * wtLeft + r[j] * wtTr + 2) >> 2);
                }
                return;
            }
            for (int i = 0; i < _arrays.Length; i++)
            {
                var l = _arrays[i];
                var r = t[i];
                for (int j = 0; j < l.Length; j++)
                    if (l[j] != r[j]) l[j] = (ushort)((l[j] * wtLeft + r[j] * wtTr + half) / sum);
            }
        }
    }
}
