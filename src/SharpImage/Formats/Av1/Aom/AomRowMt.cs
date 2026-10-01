using System;
using System.Collections.Generic;
using System.Reflection;
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
}

internal sealed partial class AomMacroblock
{
    /// <summary>The palette color map token list the final encode appends to (the current SB row's tplist entry).</summary>
    public List<AomPaletteToken> PaletteTokens = null!;
    // td->vt64x64 (the variance tree storage of the thread, reused by every superblock)
    public readonly AomVarTree VarTree = new();
    // td->pc_root of the non-RD path (allocated once per frame and thread and reused by every superblock)
    public AomPcTree? NonrdPcRoot;
}

/// <summary>Port of libaom 3.14.1's row-based multi-threading of the encode stage for one tile (ethread.c
/// av1_encode_tiles_row_mt / enc_row_mt_worker_hook / av1_row_mt_sync_read / av1_row_mt_sync_write, encodeframe.c's
/// row-MT parts of encode_sb_row, partition_search.c wait_for_top_right_sb, encodeframe_utils.c av1_avg_cdf_symbols).
/// Superblock rows run on worker threads in a wavefront: row r's superblock c starts once row r - 1 has finished
/// superblock c + 1 - delay (+ the intrabc extra delay); with CDF adaptation each row starts from the CDFs row r - 1 had
/// after its second superblock and averages in the top-right superblock's CDFs before every later superblock.</summary>
internal sealed class AomRowMt
{
    private const int AVG_CDF_WEIGHT_LEFT = 3, AVG_CDF_WEIGHT_TOP_RIGHT = 1;

    // AV1EncRowMultiThreadSync of the tile
    private readonly int[] _numFinishedCols;
    private readonly object[] _mutex;
    private readonly int _syncRange = 1;
    private readonly int _intrabcExtraTopRightSbDelay;
    // AV1EncRowMultiThreadInfo
    private readonly object _jobMutex = new();
    private int _nextMiRow;
    private volatile bool _rowMtExit;
    // this_tile->row_ctx: max(1, sb_cols - 1) frame contexts
    private readonly CdfArrays[] _rowCtx;
    private readonly int _delay;
    // the SB rows' palette token lists (tplist), concatenated in row order after the encode
    private readonly List<AomPaletteToken>[] _rowTokens;

    private AomRowMt(AomComp cpi, int sbRows, int sbCols)
    {
        _numFinishedCols = new int[sbRows];
        _mutex = new object[sbRows];
        for (int i = 0; i < sbRows; i++) { _numFinishedCols[i] = -1; _mutex[i] = new object(); }
        // av1_get_intrabc_extra_top_right_sb_delay
        _intrabcExtraTopRightSbDelay = cpi.AllowIntrabcNow ? (cpi.Cm.SbSize == BLOCK_128X128 ? 2 : 4) : 0;
        _delay = AomEncoder.DelayWaitForTopRightSb(cpi) ? 1 : 0;
        if (cpi.AllowUpdateCdf)
        {
            _rowCtx = new CdfArrays[Math.Max(1, sbCols - 1)];
            for (int i = 0; i < _rowCtx.Length; i++) _rowCtx[i] = new CdfArrays(new Av1CdfContext());
        }
        else _rowCtx = Array.Empty<CdfArrays>();
        _rowTokens = new List<AomPaletteToken>[sbRows];
        for (int i = 0; i < sbRows; i++) _rowTokens[i] = new List<AomPaletteToken>();
    }

    /// <summary>cpi->mt_info.num_workers for an all-intra frame: av1_get_max_num_workers over the modules'
    /// worker counts (av1_compute_num_workers_for_mt: one tile, one pass, row_mt 1), 0 when g_threads is 1.</summary>
    internal static int ComputeNumWorkers(AomCommon cm, int maxThreads)
    {
        if (maxThreads <= 1) return 0;
        int sbRows = (cm.MiRows + cm.MibSize - 1) >> cm.MibSizeLog2, sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2;
        int enc = Math.Min(maxThreads, Math.Min((sbCols + 1) >> 1, sbRows));   // MOD_FP / TF / TPL / ENC / LPF / CDEF / LR
        int ai = Math.Min(cm.MiRows / MiSizeWide[BLOCK_8X8], maxThreads);       // MOD_AI (compute_num_ai_workers)
        int max = Math.Max(Math.Max(enc, ai), 1);                               // MOD_GME / MOD_PACK_BS: 1
        max = Math.Min(max, maxThreads);
        return max > 1 ? max : 0;   // no workers are created for a single one
    }

    /// <summary>av1_row_mt_sync_read.</summary>
    private void SyncRead(int r, int c)
    {
        if (r == 0) return;
        int nsync = _syncRange;
        int extra = _intrabcExtraTopRightSbDelay;
        if (c <= Volatile.Read(ref _numFinishedCols[r - 1]) - nsync - extra) return;
        var mutex = _mutex[r - 1];
        lock (mutex)
        {
            while (c > _numFinishedCols[r - 1] - nsync - extra) Monitor.Wait(mutex);
        }
    }

    /// <summary>av1_row_mt_sync_write.</summary>
    private void SyncWrite(int r, int c, int cols)
    {
        int nsync = _syncRange;
        int cur;
        bool sig = true;
        if (c < cols - 1)
        {
            cur = c;
            if (c % nsync != 0) sig = false;
        }
        else cur = cols + nsync + _intrabcExtraTopRightSbDelay;
        if (!sig) return;
        var mutex = _mutex[r];
        lock (mutex)
        {
            Volatile.Write(ref _numFinishedCols[r], Math.Max(_numFinishedCols[r], cur));
            Monitor.PulseAll(mutex);
        }
    }

    /// <summary>set_encoding_done (after an error): every row reports itself finished so no worker waits forever.</summary>
    private void SetEncodingDone(int sbRows, int sbCols)
    {
        for (int r = 0; r < sbRows; r++) SyncWrite(r, sbCols - 1, sbCols);
    }

    /// <summary>wait_for_top_right_sb (rd_pick_sb_modes / pick_sb_modes_nonrd): with the top-only superblock wait
    /// (cost update frequency off / tile), the block on the superblock's top row that reaches its right edge waits for
    /// the top-right superblock.</summary>
    internal static void WaitForTopRightSb(AomComp cpi, int bsize, int miRow, int miCol)
    {
        var mt = cpi.RowMt;
        if (mt == null) return;   // av1_row_mt_sync_read_dummy
        var cm = cpi.Cm;
        int sbSizeInMi = MiSizeWide[cm.SbSize];
        int bwInMi = MiSizeWide[bsize];
        int blkRowInSb = miRow & (sbSizeInMi - 1);
        int blkColInSb = miCol & (sbSizeInMi - 1);
        bool topRightBlockInSb = blkRowInSb == 0 && blkColInSb + bwInMi >= sbSizeInMi;
        if (!topRightBlockInSb) return;
        int sbRowInTile = (miRow - cm.TileMiRowStart) >> cm.MibSizeLog2;
        int sbColInTile = (miCol - cm.TileMiColStart) >> cm.MibSizeLog2;
        mt.SyncRead(sbRowInTile, sbColInTile);
    }

    /// <summary>encode_sb_row's row-MT steps before a superblock: the top-right wait (sync_read_ptr) and, with CDF
    /// adaptation, the row's starting CDFs (restored at the first column) or the left / top-right average.
    /// Returns false when a worker has failed (row_mt_exit).</summary>
    internal bool BeforeSb(AomComp cpi, AomMacroblock x, int miRow, int miCol, int sbRow, int sbColInTile)
    {
        var cm = cpi.Cm;
        SyncRead(sbRow, sbColInTile - _delay);
        if (_rowMtExit) return false;
        bool updateCdf = cpi.AllowUpdateCdf;
        if (updateCdf && cm.TileMiRowStart != miRow)
        {
            var ctx = Arrays(x);
            if (cm.TileMiColStart == miCol)
                ctx.CopyFrom(_rowCtx[0]);   // restore frame context at the 1st column sb
            else if (cm.TileMiColEnd > miCol + cm.MibSize)
                ctx.Average(_rowCtx[sbColInTile], AVG_CDF_WEIGHT_LEFT, AVG_CDF_WEIGHT_TOP_RIGHT);
            else
                ctx.Average(_rowCtx[sbColInTile - 1], AVG_CDF_WEIGHT_LEFT, AVG_CDF_WEIGHT_TOP_RIGHT);
        }
        return true;
    }

    /// <summary>encode_sb_row's row-MT steps after a superblock: save the top-right context for the next row and
    /// report the superblock finished (sync_write_ptr).</summary>
    internal void AfterSb(AomComp cpi, AomMacroblock x, int miRow, int sbRow, int sbColInTile, int sbColsInTile)
    {
        var cm = cpi.Cm;
        if (cpi.AllowUpdateCdf && cm.TileMiRowEnd > miRow + cm.MibSize)
        {
            if (sbColsInTile == 1) _rowCtx[0].CopyFrom(Arrays(x));
            else if (sbColInTile >= 1) _rowCtx[sbColInTile - 1].CopyFrom(Arrays(x));
        }
        SyncWrite(sbRow, sbColInTile, sbColsInTile);
    }

    // the flattened view of each thread's tile_ctx
    [ThreadStatic] private static CdfArrays? t_ctxArrays;
    private static CdfArrays Arrays(AomMacroblock x)
    {
        var a = t_ctxArrays;
        if (a == null || !ReferenceEquals(a.Ctx, x.TileCtx)) t_ctxArrays = a = new CdfArrays(x.TileCtx);
        return a;
    }

    /// <summary>av1_encode_tiles_row_mt for the frame's tile: prepare_enc_workers (each worker's MACROBLOCK set up
    /// like the main one), launch the workers on the SB-row jobs, sync, and gather the rows' palette tokens.</summary>
    internal static void EncodeTilesRowMt(AomComp cpi, AomMacroblock mainX, Func<AomMacroblock> newThreadData, int maxThreads)
    {
        var cm = cpi.Cm;
        int sbRows = (cm.MiRows + cm.MibSize - 1) >> cm.MibSizeLog2, sbCols = (cm.MiCols + cm.MibSize - 1) >> cm.MibSizeLog2;
        var mt = new AomRowMt(cpi, sbRows, sbCols) { _nextMiRow = cm.TileMiRowStart };
        cpi.RowMt = mt;
        // num_mod_workers[MOD_ENC] (compute_num_enc_row_mt_workers), at most mt_info->num_workers
        int numWorkers = Math.Min(Math.Min(maxThreads, Math.Min((sbCols + 1) >> 1, sbRows)), Math.Max(cpi.NumWorkers, 1));
        numWorkers = Math.Max(numWorkers, 1);
        cm.ZeroAboveContext();

        Exception? error = null;
        using var done = new CountdownEvent(numWorkers);
        void Run(AomMacroblock? x)
        {
            try
            {
                mt.WorkerHook(cpi, x ?? newThreadData(), sbRows, sbCols);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref error, e, null);
                mt._rowMtExit = true;
                mt.SetEncodingDone(sbRows, sbCols);
            }
            finally { done.Signal(); }
        }
        for (int i = numWorkers - 1; i >= 1; i--) ThreadPool.UnsafeQueueUserWorkItem(_ => Run(null), null);
        Run(mainX);   // the main thread is worker 0 (cpi->td)
        done.Wait();
        cpi.RowMt = null;
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();

        // the token lists of the SB rows, in coding order
        cpi.PaletteTokens.Clear();
        foreach (var l in mt._rowTokens) cpi.PaletteTokens.AddRange(l);
        mainX.PaletteTokens = cpi.PaletteTokens;
    }

    /// <summary>enc_row_mt_worker_hook: take SB-row jobs until the tile has none left.</summary>
    private void WorkerHook(AomComp cpi, AomMacroblock x, int sbRows, int sbCols)
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
                // get_next_job
                if (_nextMiRow >= cm.TileMiRowEnd) return;   // end_of_frame (one tile: no other tile to switch to)
                currentMiRow = _nextMiRow;
                _nextMiRow += cm.MibSize;
            }
            int sbRow = currentMiRow >> cm.MibSizeLog2;
            x.TileCtx = tctx;
            if (cpi.AllowUpdateCdf)
            {
                if (currentMiRow == cm.TileMiRowStart) tctx.CopyFrom(cm.Fc);   // this_tile->tctx
            }
            else tctx.CopyFrom(cm.Fc);
            AomCfl.CflInit(xd.Cfl, cm.SsX, cm.SsY);
            x.PaletteTokens = _rowTokens[sbRow];
            AomEncoder.EncodeSbRow(cpi, x, currentMiRow);
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
            Collect(ctx, list);
            _arrays = list.ToArray();
        }

        private static void Collect(object o, List<ushort[]> list)
        {
            var fields = o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
            Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            foreach (var f in fields)
            {
                object? v = f.GetValue(o);
                switch (v)
                {
                    case ushort[] a: list.Add(a); break;
                    case ushort[][] aa: list.AddRange(aa); break;
                    case Av1CdfCoefContext or Av1CdfModeContext or Av1CdfMvContext or Av1CdfMvComponent: Collect(v, list); break;
                    case null: break;
                    default: throw new InvalidOperationException($"unexpected CDF context field {f.Name}");
                }
            }
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
