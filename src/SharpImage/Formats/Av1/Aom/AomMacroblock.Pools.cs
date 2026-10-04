namespace SharpImage.Formats.Av1;

using static SharpImage.Formats.Av1.AomTables;

// The partition search's per-thread allocations: libaom mallocs a PICK_MODE_CONTEXT per candidate block (av1_alloc_pmc),
// a PC_TREE node per square split (av1_alloc_pc_tree_node) and keeps PartitionSearchState / RD_SEARCH_MACROBLOCK_CONTEXT
// on the stack of rd_pick_partition. Allocating each of them afresh hands the search a stream of cache-cold memory (and
// the threads of row-MT contend for it); recycling what av1_free_pmc / av1_free_pc_tree_recursive free, and one state
// per recursion depth, keeps the search in hot memory as malloc's free lists do.
internal sealed partial class AomMacroblock
{
    private readonly Stack<AomPickModeContext>?[] _pmcPool = new Stack<AomPickModeContext>?[BLOCK_SIZES_ALL * 2];
    private readonly Stack<AomPcTree> _pcTreePool = new();
    private readonly List<AomSearchMbContext> _searchCtxs = new();
    private readonly List<AomPartitionSearchState> _partStates = new();
    /// <summary>The rd_pick_partition recursion depth (the index of the stack-like state of the current call).</summary>
    internal int PartDepth;

    /// <summary>av1_alloc_pmc: a context as freshly allocated (a freed one reset, or a new one).</summary>
    internal AomPickModeContext RentPmc(int bsize, bool allowScreenContentTools)
    {
        var pool = _pmcPool[bsize * 2 + (allowScreenContentTools ? 1 : 0)];
        if (pool != null && pool.TryPop(out var c))
        {
            c.Reset();
            return c;
        }
        return new AomPickModeContext(bsize, allowScreenContentTools);
    }

    /// <summary>av1_free_pmc: the context goes back for reuse (nothing may reference it afterwards).</summary>
    internal void ReturnPmc(AomPickModeContext c)
    {
        int key = c.Bsize * 2 + (c.AllowScreenContentTools ? 1 : 0);
        (_pmcPool[key] ??= new Stack<AomPickModeContext>()).Push(c);
    }

    /// <summary>av1_alloc_pc_tree_node.</summary>
    internal AomPcTree RentPcTree(int bsize)
    {
        if (_pcTreePool.TryPop(out var t))
        {
            t.Reset(bsize);
            return t;
        }
        return new AomPcTree(bsize);
    }

    /// <summary>aom_free(pc_tree).</summary>
    internal void ReturnPcTree(AomPcTree t) => _pcTreePool.Push(t);

    /// <summary>rd_pick_partition's RD_SEARCH_MACROBLOCK_CONTEXT x_ctx at a recursion depth (av1_save_context fills it
    /// before any restore, so it needs no reset).</summary>
    internal AomSearchMbContext SearchCtxAt(int depth)
    {
        while (_searchCtxs.Count <= depth) _searchCtxs.Add(new AomSearchMbContext());
        return _searchCtxs[depth];
    }

    /// <summary>rd_pick_partition's PartitionSearchState at a recursion depth, reset to its initial (zero) state.</summary>
    internal AomPartitionSearchState PartStateAt(int depth)
    {
        if (_partStates.Count <= depth)
        {
            while (_partStates.Count <= depth) _partStates.Add(new AomPartitionSearchState());
            return _partStates[depth];
        }
        var s = _partStates[depth];
        s.Reset();
        return s;
    }
}
