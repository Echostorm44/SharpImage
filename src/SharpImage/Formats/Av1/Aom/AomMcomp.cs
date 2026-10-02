using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

/// <summary>search_site_config: per search step, the candidate offsets (full-pel mvs; the address offset is applied
/// with the reference buffer's stride, which in libaom's all-intra encoder equals the lookahead stride the config was
/// built for).</summary>
internal sealed class AomSearchSiteConfig
{
    public const int MaxMvSearchSteps = 11;
    public readonly AomMv[,] Site = new AomMv[MaxMvSearchSteps * 2, 16 + 1];
    public readonly int[] SearchesPerStep = new int[MaxMvSearchSteps * 2];
    public readonly int[] Radius = new int[MaxMvSearchSteps * 2];
    public int NumSearchSteps;
}

/// <summary>FULLPEL_MOTION_SEARCH_PARAMS for the single-reference, non-OBMC searches (intrabc).</summary>
internal sealed class AomFullPelMsParams
{
    public int Bsize;
    public int SearchMethod;
    public AomSearchSiteConfig SearchSites = null!;
    public AomFullMvLimits MvLimits;
    public bool RunMeshSearch;
    public bool PruneMeshSearch;
    public int MeshSearchMvDiffThreshold;
    public int ForceMeshThresh;
    public AomMeshPattern[][] MeshPatterns = new AomMeshPattern[2][];
    public bool FineSearchInterval;
    public bool IsIntraMode;
    // MV_COST_PARAMS (MV_COST_ENTROPY)
    public AomMv RefMv, FullRefMv;
    public int[]? MvJCost;
    public int[][]? MvCost;
    public int ErrorPerBit, SadPerBit;
    // MSBuffers: src (the source block) and ref (the reference at the block position)
    public AomBuf2d Src, Ref;
    // sdf / sdx4df / sdx3df: the skip-row SAD (use_downsampled_sad) or the full one
    public bool SkipSad;
    // the frame bit depth (the highbd fn_ptr _bits10 / _bits12 wrappers)
    public int Bd = 8;
    public int MvCostType;   // MV_COST_ENTROPY
    // ms_buffers.second_pred / mask (compound searches)
    public bool HasSecondPred;
    public int FastObmcSearch;
}

/// <summary>FULLPEL_MV_STATS.</summary>
internal struct AomFullpelMvStats
{
    public int Distortion, ErrCost;
    public uint Sse;
}

// Port of libaom 3.14.1 av1/encoder/mcomp.c's full-pixel motion search as the intrabc search uses it
// (av1_make_default_fullpel_ms_params, av1_set_ms_to_intra_mode, av1_set_mv_search_range, the search site
// initialisations, pattern_search (HEX / BIGDIA and the fast diamonds), diamond_search_sad / full_pixel_diamond,
// exhaustive_mesh_search / full_pixel_exhaustive, av1_full_pixel_search and av1_intrabc_hash_search), plus
// motion_search_facade.h's av1_get_default_mv_search_method.
internal static class AomMcomp
{
    public const int MAX_MVSEARCH_STEPS = AomSearchSiteConfig.MaxMvSearchSteps;
    public const int MAX_FULL_PEL_VAL = (1 << (MAX_MVSEARCH_STEPS - 1)) - 1, MAX_FIRST_STEP = 1 << (MAX_MVSEARCH_STEPS - 1);
    private const int MAX_PATTERN_SCALES = 11, MAX_PATTERN_CANDIDATES = 8, PATTERN_CANDIDATES_REF = 3;

    // search_method_lookup
    public static int SearchMethodLookupOf(int m) => SearchMethodLookup[m];
    private static readonly int[] SearchMethodLookup = { DIAMOND, NSTEP, NSTEP_8PT, CLAMPED_DIAMOND, HEX, BIGDIA, BIGDIA, BIGDIA, BIGDIA };

    /// <summary>av1_init_search_range.</summary>
    public static int InitSearchRange(int size)
    {
        int sr = 0;
        size = Math.Max(16, size);
        while ((size << sr) < MAX_FULL_PEL_VAL) sr++;
        return Math.Min(sr, MAX_MVSEARCH_STEPS - 2);
    }

    // ---- search site initialisation ----

    private static void SetSite(AomSearchSiteConfig cfg, int stage, int i, int row, int col) => cfg.Site[stage, i] = new AomMv(row, col);

    /// <summary>init_dsmotion_compensation (level 0: DIAMOND, 1: CLAMPED_DIAMOND).</summary>
    private static void InitDsMotionCompensation(AomSearchSiteConfig cfg, int level)
    {
        int numSearchSteps = 0;
        int stageIndex = MAX_MVSEARCH_STEPS - 1;
        cfg.Site[stageIndex, 0] = default;
        int firstStep = level > 0 ? MAX_FIRST_STEP / 4 : MAX_FIRST_STEP;
        for (int radius = firstStep; radius > 0;)
        {
            int numSearchPts = 8;
            int[] m = { 0, 0, -radius, 0, radius, 0, 0, -radius, 0, radius, -radius, -radius, radius, radius, -radius, radius, radius, -radius };
            for (int i = 0; i <= numSearchPts; ++i) SetSite(cfg, stageIndex, i, m[2 * i], m[2 * i + 1]);
            cfg.SearchesPerStep[stageIndex] = numSearchPts;
            cfg.Radius[stageIndex] = radius;
            if (level == 0 || (stageIndex < 9 && level != 0)) radius /= 2;
            --stageIndex;
            ++numSearchSteps;
        }
        cfg.NumSearchSteps = numSearchSteps;
    }

    /// <summary>init_motion_compensation_nstep (level 0: NSTEP, 1: NSTEP_8PT).</summary>
    private static void InitMotionCompensationNstep(AomSearchSiteConfig cfg, int level)
    {
        int numSearchSteps = 0;
        int radius = 1;
        int numStages = level > 0 ? 16 : 15;
        for (int stageIndex = 0; stageIndex < numStages; ++stageIndex)
        {
            int tanRadius = Math.Max((int)(0.41 * radius), 1);
            int numSearchPts = 12;
            if (radius <= 5 || level > 0)
            {
                tanRadius = radius;
                numSearchPts = 8;
            }
            int[] m =
            {
                0, 0, -radius, 0, radius, 0, 0, -radius, 0, radius, -radius, -tanRadius, radius, tanRadius, -tanRadius, radius,
                tanRadius, -radius, -radius, tanRadius, radius, -tanRadius, tanRadius, radius, -tanRadius, -radius,
            };
            for (int i = 0; i <= numSearchPts; ++i) SetSite(cfg, stageIndex, i, m[2 * i], m[2 * i + 1]);
            cfg.SearchesPerStep[stageIndex] = numSearchPts;
            cfg.Radius[stageIndex] = radius;
            ++numSearchSteps;
            if (stageIndex < 12) radius = (int)Math.Max(radius * 1.5 + 0.5, radius + 1);
        }
        cfg.NumSearchSteps = numSearchSteps;
    }

    private static readonly int[] BigdiaNumCandidates = { 4, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8 };
    private static readonly int[] BigdiaCandidates =   // [scale][8][row, col]
    {
        0, -1, 1, 0, 0, 1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -1, -1, 0, -2, 1, -1, 2, 0, 1, 1, 0, 2, -1, 1, -2, 0,
        -2, -2, 0, -4, 2, -2, 4, 0, 2, 2, 0, 4, -2, 2, -4, 0,
        -4, -4, 0, -8, 4, -4, 8, 0, 4, 4, 0, 8, -4, 4, -8, 0,
        -8, -8, 0, -16, 8, -8, 16, 0, 8, 8, 0, 16, -8, 8, -16, 0,
        -16, -16, 0, -32, 16, -16, 32, 0, 16, 16, 0, 32, -16, 16, -32, 0,
        -32, -32, 0, -64, 32, -32, 64, 0, 32, 32, 0, 64, -32, 32, -64, 0,
        -64, -64, 0, -128, 64, -64, 128, 0, 64, 64, 0, 128, -64, 64, -128, 0,
        -128, -128, 0, -256, 128, -128, 256, 0, 128, 128, 0, 256, -128, 128, -256, 0,
        -256, -256, 0, -512, 256, -256, 512, 0, 256, 256, 0, 512, -256, 256, -512, 0,
        -512, -512, 0, -1024, 512, -512, 1024, 0, 512, 512, 0, 1024, -512, 512, -1024, 0,
    };

    /// <summary>init_motion_compensation_bigdia.</summary>
    private static void InitMotionCompensationBigdia(AomSearchSiteConfig cfg)
    {
        int radius = 1;
        for (int i = 0; i < MAX_PATTERN_SCALES; ++i)
        {
            cfg.SearchesPerStep[i] = BigdiaNumCandidates[i];
            cfg.Radius[i] = radius;
            for (int j = 0; j < MAX_PATTERN_CANDIDATES; ++j)
                SetSite(cfg, i, j, BigdiaCandidates[(i * 8 + j) * 2], BigdiaCandidates[(i * 8 + j) * 2 + 1]);
            radius *= 2;
        }
        cfg.NumSearchSteps = MAX_PATTERN_SCALES;
    }

    private static readonly int[] HexNumCandidates = { 8, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6 };
    private static readonly int[] HexCandidates =   // [scale][8][row, col]
    {
        -1, -1, 0, -1, 1, -1, 1, 0, 1, 1, 0, 1, -1, 1, -1, 0,
        -1, -2, 1, -2, 2, 0, 1, 2, -1, 2, -2, 0, 0, 0, 0, 0,
        -2, -4, 2, -4, 4, 0, 2, 4, -2, 4, -4, 0, 0, 0, 0, 0,
        -4, -8, 4, -8, 8, 0, 4, 8, -4, 8, -8, 0, 0, 0, 0, 0,
        -8, -16, 8, -16, 16, 0, 8, 16, -8, 16, -16, 0, 0, 0, 0, 0,
        -16, -32, 16, -32, 32, 0, 16, 32, -16, 32, -32, 0, 0, 0, 0, 0,
        -32, -64, 32, -64, 64, 0, 32, 64, -32, 64, -64, 0, 0, 0, 0, 0,
        -64, -128, 64, -128, 128, 0, 64, 128, -64, 128, -128, 0, 0, 0, 0, 0,
        -128, -256, 128, -256, 256, 0, 128, 256, -128, 256, -256, 0, 0, 0, 0, 0,
        -256, -512, 256, -512, 512, 0, 256, 512, -256, 512, -512, 0, 0, 0, 0, 0,
        -512, -1024, 512, -1024, 1024, 0, 512, 1024, -512, 1024, -1024, 0, 0, 0, 0, 0,
    };

    /// <summary>init_motion_compensation_hex.</summary>
    private static void InitMotionCompensationHex(AomSearchSiteConfig cfg)
    {
        int radius = 1;
        for (int i = 0; i < MAX_PATTERN_SCALES; ++i)
        {
            cfg.SearchesPerStep[i] = HexNumCandidates[i];
            cfg.Radius[i] = radius;
            for (int j = 0; j < HexNumCandidates[i]; ++j)
                SetSite(cfg, i, j, HexCandidates[(i * 8 + j) * 2], HexCandidates[(i * 8 + j) * 2 + 1]);
            radius *= 2;
        }
        cfg.NumSearchSteps = MAX_PATTERN_SCALES;
    }

    /// <summary>init_motion_estimation's search_site_cfg[SS_CFG_LOOKAHEAD] for the NUM_DISTINCT_SEARCH_METHODS.</summary>
    public static AomSearchSiteConfig[] InitSearchSites()
    {
        var cfgs = new AomSearchSiteConfig[NUM_DISTINCT_SEARCH_METHODS];
        for (int i = DIAMOND; i < NUM_DISTINCT_SEARCH_METHODS; i++)
        {
            cfgs[i] = new AomSearchSiteConfig();
            int level = i == NSTEP_8PT || i == CLAMPED_DIAMOND ? 1 : 0;
            switch (i)
            {
                case DIAMOND: case CLAMPED_DIAMOND: InitDsMotionCompensation(cfgs[i], level); break;
                case NSTEP: case NSTEP_8PT: InitMotionCompensationNstep(cfgs[i], level); break;
                case HEX: InitMotionCompensationHex(cfgs[i]); break;
                default: InitMotionCompensationBigdia(cfgs[i]); break;
            }
        }
        return cfgs;
    }

    /// <summary>av1_get_faster_search_method.</summary>
    private static int GetFasterSearchMethod(int searchMethod) => searchMethod switch
    {
        NSTEP => DIAMOND, NSTEP_8PT => DIAMOND, DIAMOND => BIGDIA, CLAMPED_DIAMOND => BIGDIA, BIGDIA => HEX, HEX => FAST_DIAMOND,
        FAST_DIAMOND => VFAST_DIAMOND, FAST_BIGDIA => FAST_BIGDIA, VFAST_DIAMOND => VFAST_DIAMOND, _ => DIAMOND,
    };

    /// <summary>av1_get_default_mv_search_method (the content-state variant 4 is real-time only).</summary>
    public static int GetDefaultMvSearchMethod(AomMacroblock x, AomMvSpeedFeatures mvSf, int bsize)
    {
        int searchMethod = mvSf.search_method;
        int sfBlkSearchMethod = mvSf.use_bsize_dependent_search_method;
        int minDim = Math.Min(BlockSizeWide[bsize], BlockSizeHigh[bsize]);
        ReadOnlySpan<int> minDimTh = stackalloc int[] { 128, 64, 32, 16 };
        bool useFaster = false;
        if (sfBlkSearchMethod >= 1 && sfBlkSearchMethod <= 3) useFaster = minDim >= minDimTh[sfBlkSearchMethod - 1];
        else if (sfBlkSearchMethod == 4) throw new NotSupportedException("use_bsize_dependent_search_method 4 (real-time content state)");
        if (useFaster) searchMethod = GetFasterSearchMethod(searchMethod);
        return searchMethod;
    }

    /// <summary>av1_set_mv_search_range.</summary>
    public static void SetMvSearchRange(ref AomFullMvLimits l, AomMv mv)
    {
        int colMin = ((mv.Col + 7) >> 3) - MAX_FULL_PEL_VAL;
        int rowMin = ((mv.Row + 7) >> 3) - MAX_FULL_PEL_VAL;
        int colMax = (mv.Col >> 3) + MAX_FULL_PEL_VAL;
        int rowMax = (mv.Row >> 3) + MAX_FULL_PEL_VAL;
        colMin = Math.Max(colMin, (AomMvCost.MvLow >> 3) + 1);
        rowMin = Math.Max(rowMin, (AomMvCost.MvLow >> 3) + 1);
        colMax = Math.Min(colMax, (AomMvCost.MvUpp >> 3) - 1);
        rowMax = Math.Min(rowMax, (AomMvCost.MvUpp >> 3) - 1);
        l.ColMin = Math.Max(l.ColMin, colMin);
        l.ColMax = Math.Min(l.ColMax, colMax);
        l.RowMin = Math.Max(l.RowMin, rowMin);
        l.RowMax = Math.Min(l.RowMax, rowMax);
        l.ColMax = Math.Max(l.ColMin, l.ColMax);
        l.RowMax = Math.Max(l.RowMin, l.RowMax);
    }

    /// <summary>av1_is_fullmv_in_range.</summary>
    public static bool IsFullmvInRange(in AomFullMvLimits l, AomMv mv)
        => mv.Col >= l.ColMin && mv.Col <= l.ColMax && mv.Row >= l.RowMin && mv.Row <= l.RowMax;

    private static AomMv ClampFullmv(AomMv mv, in AomFullMvLimits l)
        => new(Math.Clamp((int)mv.Row, l.RowMin, l.RowMax), Math.Clamp((int)mv.Col, l.ColMin, l.ColMax));

    /// <summary>av1_make_default_fullpel_ms_params (key frame: use_downsampled_sad 1 does nothing; sharpness 3 unsupported)
    /// followed by the caller's buffers. mvLimits is x->mv_limits (the intrabc search overwrites the limits anyway).</summary>
    public static AomFullPelMsParams MakeDefaultFullpelMsParams(AomComp cpi, AomMacroblock x, int bsize, AomMv refMv,
        AomSearchSiteConfig[] searchSites, int searchMethod, bool fineSearchInterval, in AomFullMvLimits mvLimits, AomMv? startMvOpt = null)
    {
        var mvSf = cpi.Sf.mv_sf;
        var p = new AomFullPelMsParams { Bsize = bsize };
        p.Src = x.Plane[0].Src;
        p.Ref = x.E.Plane[0].Pre0;
        p.Bd = x.E.Bd;
        p.SearchMethod = searchMethod;
        p.SearchSites = searchSites[SearchMethodLookup[searchMethod]];
        p.MeshPatterns[0] = mvSf.mesh_patterns;
        p.MeshPatterns[1] = mvSf.intrabc_mesh_patterns;
        p.ForceMeshThresh = mvSf.exhaustive_searches_thresh;
        p.PruneMeshSearch = mvSf.prune_mesh_search == PRUNE_MESH_SEARCH_LVL_2;
        p.MeshSearchMvDiffThreshold = 4;
        p.RunMeshSearch = false;
        p.FineSearchInterval = fineSearchInterval;
        p.IsIntraMode = false;
        p.MvLimits = mvLimits;
        SetMvSearchRange(ref p.MvLimits, refMv);
        if (cpi.Sharpness == 3) throw new NotSupportedException("sharpness 3 motion search margins");
        // init_mv_cost_params: all-intra has no x->mv_costs; the intrabc search sets the dv costs
        p.RefMv = refMv;
        p.FullRefMv = refMv.ToFullMv();
        p.ErrorPerBit = x.Errorperbit;
        p.SadPerBit = x.SadPerBit;
        p.FastObmcSearch = mvSf.obmc_full_pixel_search_level;
        if (cpi.Mode != ALLINTRA)
        {
            // x->mv_costs (allocated outside the all-intra mode)
            p.MvJCost = x.MvCosts.NmvJointCost;
            p.MvCost = x.MvCosts.MvCostStack;
        }
        p.SkipSad = mvSf.use_downsampled_sad == 2 && BlockSizeHigh[bsize] >= 16;
        bool isKeyFrame = cpi.UpdateType == KF_UPDATE;
        if (!p.SkipSad && mvSf.use_downsampled_sad == 1 && BlockSizeHigh[bsize] >= 16 && !isKeyFrame && startMvOpt is AomMv startMv)
        {
            var c = ClampFullmv(startMv, p.MvLimits);
            int off = RefOffset(p.Ref, c.Row, c.Col);
            int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
            uint even, odd;
            if (p.Src.Buf16 != null)
            {
                even = AomHbd.Sad(p.Src.Buf16, p.Src.Offset, 2 * p.Src.Stride, p.Ref.Buf16!, off, 2 * p.Ref.Stride, w, h / 2) * 2 >> (p.Bd - 8);
                odd = AomHbd.Sad(p.Src.Buf16, p.Src.Offset + p.Src.Stride, 2 * p.Src.Stride, p.Ref.Buf16!, off + p.Ref.Stride, 2 * p.Ref.Stride, w, h / 2) * 2 >> (p.Bd - 8);
            }
            else
            {
                even = AomSad.SadSkip(p.Src.Buf, p.Src.Offset, p.Src.Stride, p.Ref.Buf, off, p.Ref.Stride, w, h);
                odd = AomSad.SadSkip(p.Src.Buf, p.Src.Offset + p.Src.Stride, p.Src.Stride, p.Ref.Buf, off + p.Ref.Stride, p.Ref.Stride, w, h);
            }
            int diff = Math.Abs((int)even - (int)odd);
            if (diff * 4 < (int)even) p.SkipSad = true;
        }
        return p;
    }

    /// <summary>av1_set_ms_to_intra_mode.</summary>
    public static void SetMsToIntraMode(AomFullPelMsParams p, AomDvCosts dv)
    {
        p.IsIntraMode = true;
        p.MvJCost = dv.JointMv;
        p.MvCost = dv.DvCosts;
    }

    // ---- distortions ----

    private static int RefOffset(in AomBuf2d r, int row, int col) => r.Offset + row * r.Stride + col;

    private static uint SdfAt(AomFullPelMsParams p, int refOff, bool skip)
    {
        int w = BlockSizeWide[p.Bsize], h = BlockSizeHigh[p.Bsize];
        if (p.Src.Buf16 != null)
            return (skip ? 2 * AomHbd.Sad(p.Src.Buf16, p.Src.Offset, 2 * p.Src.Stride, p.Ref.Buf16!, refOff, 2 * p.Ref.Stride, w, h / 2)
                         : AomHbd.Sad(p.Src.Buf16, p.Src.Offset, p.Src.Stride, p.Ref.Buf16!, refOff, p.Ref.Stride, w, h)) >> (p.Bd - 8);
        return skip ? AomSad.SadSkip(p.Src.Buf, p.Src.Offset, p.Src.Stride, p.Ref.Buf, refOff, p.Ref.Stride, w, h)
                    : AomSad.Sad(p.Src.Buf, p.Src.Offset, p.Src.Stride, p.Ref.Buf, refOff, p.Ref.Stride, w, h);
    }

    /// <summary>get_mvpred_sad (ms_params->sdf).</summary>
    private static uint GetMvpredSad(AomFullPelMsParams p, int refOff) => SdfAt(p, refOff, p.SkipSad);

    /// <summary>get_mvpred_compound_sad.</summary>
    private static uint GetMvpredCompoundSad(AomFullPelMsParams p, int refOff)
    {
        if (p.HasSecondPred) throw new NotImplementedException("compound motion search sad");
        return GetMvpredSad(p, refOff);
    }

    public const int MV_COST_ENTROPY = 0, MV_COST_L1_LOWRES = 1, MV_COST_L1_MIDRES = 2, MV_COST_L1_HDRES = 3, MV_COST_NONE = 4;

    /// <summary>mvsad_err_cost_.</summary>
    private static int MvsadErrCost(AomFullPelMsParams p, AomMv mv)
    {
        int dr = (mv.Row - p.FullRefMv.Row) * 8, dc = (mv.Col - p.FullRefMv.Col) * 8;
        switch (p.MvCostType)
        {
            case MV_COST_ENTROPY: return p.MvCost == null ? 0 : AomMvCost.MvSadErrCost(mv, p.FullRefMv, p.MvJCost!, p.MvCost, p.SadPerBit);
            case MV_COST_L1_LOWRES: return (32 * (Math.Abs(dr) + Math.Abs(dc))) >> 3;
            case MV_COST_L1_MIDRES: return (15 * (Math.Abs(dr) + Math.Abs(dc))) >> 3;
            case MV_COST_L1_HDRES: return (8 * (Math.Abs(dr) + Math.Abs(dc))) >> 3;
            default: return 0;
        }
    }

    /// <summary>mv_err_cost_.</summary>
    internal static int MvErrCost(AomFullPelMsParams p, AomMv mv) => MvErrCost(p.MvCostType, mv, p.RefMv, p.MvJCost, p.MvCost, p.ErrorPerBit);

    /// <summary>mv_err_cost.</summary>
    internal static int MvErrCost(int type, AomMv mv, AomMv refMv, int[]? mvjcost, int[][]? mvcost, int errorPerBit)
    {
        int ar = Math.Abs(mv.Row - refMv.Row), ac = Math.Abs(mv.Col - refMv.Col);
        switch (type)
        {
            case MV_COST_ENTROPY: return mvcost == null ? 0 : AomMvCost.MvErrCost(mv, refMv, mvjcost!, mvcost, errorPerBit);
            case MV_COST_L1_LOWRES: return (2 * (ar + ac)) >> 3;
            case MV_COST_L1_MIDRES: return 0;
            case MV_COST_L1_HDRES: return (1 * (ar + ac)) >> 3;
            default: return 0;
        }
    }

    /// <summary>get_mvpred_var_cost (also get_mvpred_compound_var_cost without a second prediction).</summary>
    public static int GetMvpredVarCost(AomFullPelMsParams p, AomMv thisMv) => GetMvpredVarCost(p, thisMv, out _);

    public static int GetMvpredVarCost(AomFullPelMsParams p, AomMv thisMv, out AomFullpelMvStats stats)
    {
        if (p.HasSecondPred) throw new NotImplementedException("compound motion search variance");
        int w = BlockSizeWide[p.Bsize], h = BlockSizeHigh[p.Bsize];
        uint sse;
        uint var = p.Src.Buf16 != null
            ? AomHbd.Variance(p.Src.Buf16, p.Src.Offset, p.Src.Stride, p.Ref.Buf16, RefOffset(p.Ref, thisMv.Row, thisMv.Col), p.Ref.Stride, 0, w, h,
                p.Bd, out sse)
            : AomSad.Variance(p.Src.Buf, p.Src.Offset, p.Src.Stride, p.Ref.Buf, RefOffset(p.Ref, thisMv.Row, thisMv.Col), p.Ref.Stride,
            w, h, out sse);
        stats.Sse = sse;
        stats.Distortion = (int)var;
        stats.ErrCost = MvErrCost(p, thisMv.ToMv());
        return (int)var + stats.ErrCost;
    }

    /// <summary>update_mvs_and_sad.</summary>
    private static bool UpdateMvsAndSad(AomFullPelMsParams p, uint thisSad, AomMv mv, ref uint bestSad, ref uint rawBestSad, ref AomMv bestMv)
    {
        AomMv dummy = default;
        return UpdateMvsAndSad(p, thisSad, mv, ref bestSad, ref rawBestSad, ref bestMv, ref dummy, false);
    }

    private static bool UpdateMvsAndSad(AomFullPelMsParams p, uint thisSad, AomMv mv, ref uint bestSad, ref uint rawBestSad, ref AomMv bestMv,
        ref AomMv secondBestMv, bool trackSecond)
    {
        if (thisSad >= bestSad) return false;
        uint sad = thisSad + (uint)MvsadErrCost(p, mv);
        if (sad < bestSad)
        {
            rawBestSad = thisSad;
            bestSad = sad;
            if (trackSecond) secondBestMv = bestMv;
            bestMv = mv;
            return true;
        }
        return false;
    }

    private static bool CheckBounds(in AomFullMvLimits l, int row, int col, int range)
        => (row - range) >= l.RowMin && (row + range) <= l.RowMax && (col - range) >= l.ColMin && (col + range) <= l.ColMax;

    // ---- cost lists ----

    private static readonly AomMv[] CostListNeighbors = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };

    /// <summary>calc_int_sad_list (USE_SAD_COSTLIST).</summary>
    private static void CalcIntSadList(AomMv bestMv, AomFullPelMsParams p, int[] costList, bool costlistHasSad)
    {
        int br = bestMv.Row, bc = bestMv.Col;
        if (!costlistHasSad)
        {
            costList[0] = (int)GetMvpredSad(p, RefOffset(p.Ref, br, bc));
            if (CheckBounds(p.MvLimits, br, bc, 1))
            {
                for (int i = 0; i < 4; i++)
                    costList[i + 1] = (int)GetMvpredSad(p, RefOffset(p.Ref, br + CostListNeighbors[i].Row, bc + CostListNeighbors[i].Col));
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    var m = new AomMv(br + CostListNeighbors[i].Row, bc + CostListNeighbors[i].Col);
                    costList[i + 1] = !IsFullmvInRange(p.MvLimits, m) ? int.MaxValue : (int)GetMvpredSad(p, RefOffset(p.Ref, m.Row, m.Col));
                }
            }
        }
        costList[0] += MvsadErrCost(p, bestMv);
        for (int idx = 0; idx < 4; idx++)
            if (costList[idx + 1] != int.MaxValue)
                costList[idx + 1] += MvsadErrCost(p, new AomMv(br + CostListNeighbors[idx].Row, bc + CostListNeighbors[idx].Col));
    }

    // ---- pattern search (HEX / BIGDIA / FAST_DIAMOND / FAST_BIGDIA / VFAST_DIAMOND) ----

    // calc_sad_update_bestmv over candidates [candStart, numCandidates) (sad4 / sad3 give the same sums); with a cost
    // list the raw sads land at cost_list[i + 1]
    private static void CalcSadUpdateBestmv(AomFullPelMsParams p, ref AomMv bestMv, AomMv center, int centerOff, ref uint bestSad,
        ref uint rawBestSad, int step, ref int bestSite, int numCandidates, int candStart, bool checkRange, int[]? costList = null)
    {
        var cfg = p.SearchSites;
        for (int i = candStart; i < numCandidates; i++)
        {
            var s = cfg.Site[step, i];
            var thisMv = new AomMv(center.Row + s.Row, center.Col + s.Col);
            if (checkRange && !IsFullmvInRange(p.MvLimits, thisMv)) continue;
            uint thisSad = GetMvpredSad(p, centerOff + s.Row * p.Ref.Stride + s.Col);
            if (costList != null) costList[i + 1] = (int)thisSad;
            if (UpdateMvsAndSad(p, thisSad, thisMv, ref bestSad, ref rawBestSad, ref bestMv)) bestSite = i;
        }
    }

    // one scale of pattern_search: in bounds, calc_sad4_update_bestmv over the groups of 4, then
    // calc_sad_update_bestmv(..., remaining_cand, no_of_4_cand_loops * 4) whose loop runs i from the start up to the
    // REMAINING COUNT, so libaom never evaluates the remainder (the HEX scales' last 2 of 6 candidates) when in bounds;
    // out of bounds, every candidate with the range check
    private static void ScaleCandidates(AomFullPelMsParams p, ref AomMv bestMv, AomMv center, int centerOff, ref uint bestSad,
        ref uint rawBestSad, int step, ref int bestSite, int numCandidates, bool inBounds)
    {
        if (!inBounds)
        {
            CalcSadUpdateBestmv(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, step, ref bestSite, numCandidates, 0, true);
            return;
        }
        int loops4 = numCandidates >> 2;
        CalcSadUpdateBestmv(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, step, ref bestSite, loops4 * 4, 0, false);
        CalcSadUpdateBestmv(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, step, ref bestSite, numCandidates % 4, loops4 * 4, true);
    }

    // calc_sad3_update_bestmv / calc_sad_update_bestmv_with_indices: best_site is the index into chkpts; with a cost
    // list, cost_list[index + 1] gets the raw sad (INT_MAX out of range)
    private static void CalcSadUpdateBestmvWithIndices(AomFullPelMsParams p, ref AomMv bestMv, AomMv center, int centerOff, ref uint bestSad,
        ref uint rawBestSad, int step, ref int bestSite, ReadOnlySpan<int> chkpts, bool checkRange, int[]? costList = null)
    {
        var cfg = p.SearchSites;
        for (int i = 0; i < chkpts.Length; i++)
        {
            int index = chkpts[i];
            var s = cfg.Site[step, index];
            var thisMv = new AomMv(center.Row + s.Row, center.Col + s.Col);
            if (checkRange && !IsFullmvInRange(p.MvLimits, thisMv))
            {
                if (costList != null) costList[index + 1] = int.MaxValue;
                continue;
            }
            uint thisSad = GetMvpredSad(p, centerOff + s.Row * p.Ref.Stride + s.Col);
            if (costList != null) costList[index + 1] = (int)thisSad;
            if (UpdateMvsAndSad(p, thisSad, thisMv, ref bestSad, ref rawBestSad, ref bestMv)) bestSite = i;
        }
    }

    /// <summary>pattern_search.</summary>
    private static int PatternSearch(AomMv startMv, AomFullPelMsParams p, int searchStep, bool doInitSearch, int[]? costList, out AomMv bestMv,
        out AomFullpelMvStats bestMvStats)
    {
        ReadOnlySpan<int> searchSteps = stackalloc int[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
        var cfg = p.SearchSites;
        var numCandidates = cfg.SearchesPerStep;
        int refStride = p.Ref.Stride;
        bool lastIs4 = numCandidates[0] == 4;
        uint bestSad, rawBestSad;
        int k = -1;
        searchStep = Math.Min(searchStep, MAX_MVSEARCH_STEPS - 1);
        int bestInitS = searchSteps[searchStep];
        startMv = ClampFullmv(startMv, p.MvLimits);
        int br = startMv.Row, bc = startMv.Col;
        if (costList != null) costList[0] = costList[1] = costList[2] = costList[3] = costList[4] = int.MaxValue;
        bool costlistHasSad = false;
        bestMv = startMv;   // (libaom leaves *best_mv untouched until a better candidate; it is only read as the second best)
        rawBestSad = GetMvpredSad(p, RefOffset(p.Ref, startMv.Row, startMv.Col));
        bestSad = rawBestSad + (uint)MvsadErrCost(p, startMv);
        int centerOff = RefOffset(p.Ref, startMv.Row, startMv.Col);
        int s;
        if (doInitSearch)
        {
            s = bestInitS;
            bestInitS = -1;
            for (int t = 0; t <= s; ++t)
            {
                int bestSite = -1;
                var center = new AomMv(br, bc);
                bool inb = CheckBounds(p.MvLimits, br, bc, 1 << t);
                ScaleCandidates(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, t, ref bestSite, numCandidates[t], inb);
                if (bestSite == -1) continue;
                bestInitS = t;
                k = bestSite;
            }
            if (bestInitS != -1)
            {
                var st = cfg.Site[bestInitS, k];
                br += st.Row; bc += st.Col;
                centerOff += st.Row * refStride + st.Col;
            }
        }
        if (bestInitS != -1)
        {
            int lastS = lastIs4 && costList != null ? 1 : 0;
            int bestSite = -1;
            Span<int> next = stackalloc int[PATTERN_CANDIDATES_REF];
            s = bestInitS;
            for (; s >= lastS; s--)
            {
                if (!doInitSearch || s != bestInitS)
                {
                    var center = new AomMv(br, bc);
                    bool inb = CheckBounds(p.MvLimits, br, bc, 1 << s);
                    ScaleCandidates(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, s, ref bestSite, numCandidates[s], inb);
                    if (bestSite == -1) continue;
                    var st = cfg.Site[s, bestSite];
                    br += st.Row; bc += st.Col;
                    centerOff += st.Row * refStride + st.Col;
                    k = bestSite;
                }
                do
                {
                    bestSite = -1;
                    next[0] = k == 0 ? numCandidates[s] - 1 : k - 1;
                    next[1] = k;
                    next[2] = k == numCandidates[s] - 1 ? 0 : k + 1;
                    var center = new AomMv(br, bc);
                    bool inb = CheckBounds(p.MvLimits, br, bc, 1 << s);
                    CalcSadUpdateBestmvWithIndices(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, s, ref bestSite, next, !inb);
                    if (bestSite != -1)
                    {
                        k = next[bestSite];
                        var st = cfg.Site[s, k];
                        br += st.Row; bc += st.Col;
                        centerOff += st.Row * refStride + st.Col;
                    }
                } while (bestSite != -1);
            }
            if (s == 0)
            {
                costList![0] = (int)rawBestSad;
                costlistHasSad = true;
                if (!doInitSearch || s != bestInitS)
                {
                    var center = new AomMv(br, bc);
                    bool inb = CheckBounds(p.MvLimits, br, bc, 1 << s);
                    // calc_sad4_update_bestmv (in bounds) / calc_sad_update_bestmv (4 candidates from 0), both with the cost list
                    CalcSadUpdateBestmv(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, s, ref bestSite, 4, 0, !inb, costList);
                    if (bestSite != -1)
                    {
                        var st = cfg.Site[s, bestSite];
                        br += st.Row; bc += st.Col;
                        centerOff += st.Row * refStride + st.Col;
                        k = bestSite;
                    }
                }
                while (bestSite != -1)
                {
                    bestSite = -1;
                    next[0] = k == 0 ? numCandidates[s] - 1 : k - 1;
                    next[1] = k;
                    next[2] = k == numCandidates[s] - 1 ? 0 : k + 1;
                    costList[1] = costList[2] = costList[3] = costList[4] = int.MaxValue;
                    costList[((k + 2) % 4) + 1] = costList[0];
                    costList[0] = (int)rawBestSad;
                    var center = new AomMv(br, bc);
                    bool inb = CheckBounds(p.MvLimits, br, bc, 1 << s);
                    CalcSadUpdateBestmvWithIndices(p, ref bestMv, center, centerOff, ref bestSad, ref rawBestSad, s, ref bestSite, next, !inb, costList);
                    if (bestSite != -1)
                    {
                        k = next[bestSite];
                        var st = cfg.Site[s, k];
                        br += st.Row; bc += st.Col;
                        centerOff += st.Row * refStride + st.Col;
                    }
                }
            }
        }
        bestMv = new AomMv(br, bc);
        if (costList != null) CalcIntSadList(bestMv, p, costList, costlistHasSad);
        return GetMvpredVarCost(p, bestMv, out bestMvStats);
    }

    // ---- diamond search ----

    /// <summary>diamond_search_sad.</summary>
    private static uint DiamondSearchSad(AomMv startMv, uint startMvSad, AomFullPelMsParams p, int searchStep, out int num00, out AomMv bestMv,
        ref AomMv secondBestMv, bool trackSecond)
    {
        var cfg = p.SearchSites;
        int refStride = p.Ref.Stride;
        bool isOffCenter = false;
        int numCenterSteps = 0;
        int totSteps = cfg.NumSearchSteps - searchStep;
        AomMv tmpSecondBestMv = trackSecond ? secondBestMv : default;
        bestMv = startMv;
        int bestAddress = RefOffset(p.Ref, startMv.Row, startMv.Col);
        uint bestSad = startMvSad;
        for (int step = totSteps - 1; step >= 0; --step)
        {
            int numSearches = cfg.SearchesPerStep[step];
            int bestSite = 0;
            bool allIn = !p.HasSecondPred &&
                         bestMv.Row + cfg.Site[step, 1].Row >= p.MvLimits.RowMin && bestMv.Row + cfg.Site[step, 2].Row <= p.MvLimits.RowMax &&
                         bestMv.Col + cfg.Site[step, 3].Col >= p.MvLimits.ColMin && bestMv.Col + cfg.Site[step, 4].Col <= p.MvLimits.ColMax;
            if (allIn)
            {
                for (int idx = 1; idx <= numSearches; idx += 4)
                    for (int j = 0; j < 4; j++)
                    {
                        var s = cfg.Site[step, idx + j];
                        uint sad = GetMvpredSad(p, s.Row * refStride + s.Col + bestAddress);
                        // update_best_site
                        if (sad < bestSad)
                        {
                            var thisMv = new AomMv(bestMv.Row + s.Row, bestMv.Col + s.Col);
                            uint thisSad = sad + (uint)MvsadErrCost(p, thisMv);
                            if (thisSad < bestSad) { bestSad = thisSad; bestSite = idx + j; }
                        }
                    }
            }
            else
            {
                for (int idx = 1; idx <= numSearches; idx++)
                {
                    var s = cfg.Site[step, idx];
                    var thisMv = new AomMv(bestMv.Row + s.Row, bestMv.Col + s.Col);
                    if (!IsFullmvInRange(p.MvLimits, thisMv)) continue;
                    uint thisSad = GetMvpredCompoundSad(p, s.Row * refStride + s.Col + bestAddress);
                    if (thisSad < bestSad)
                    {
                        thisSad += (uint)MvsadErrCost(p, thisMv);
                        if (thisSad < bestSad) { bestSad = thisSad; bestSite = idx; }
                    }
                }
            }
            // UPDATE_SEARCH_STEP
            if (bestSite != 0)
            {
                tmpSecondBestMv = bestMv;
                var s = cfg.Site[step, bestSite];
                bestMv = new AomMv(bestMv.Row + s.Row, bestMv.Col + s.Col);
                bestAddress += s.Row * refStride + s.Col;
                isOffCenter = true;
            }
            if (!isOffCenter) numCenterSteps++;
            if (bestSite == 0 && step > 2)
            {
                int nextStepSize = cfg.Radius[step - 1];
                while (nextStepSize == cfg.Radius[step] && step > 2)
                {
                    numCenterSteps++;
                    --step;
                    nextStepSize = cfg.Radius[step - 1];
                }
            }
        }
        num00 = numCenterSteps;
        if (trackSecond) secondBestMv = tmpSecondBestMv;
        return bestSad;
    }

    /// <summary>full_pixel_diamond.</summary>
    private static int FullPixelDiamond(AomMv startMv, AomFullPelMsParams p, int stepParam, int[]? costList, out AomMv bestMv,
        out AomFullpelMvStats bestMvStats, ref AomMv secondBestMv, bool trackSecond)
    {
        var cfg = p.SearchSites;
        startMv = ClampFullmv(startMv, p.MvLimits);
        // get_start_mvpred_sad_cost
        uint startMvSad = (uint)MvsadErrCost(p, startMv) + GetMvpredCompoundSad(p, RefOffset(p.Ref, startMv.Row, startMv.Col));
        DiamondSearchSad(startMv, startMvSad, p, stepParam, out int n, out bestMv, ref secondBestMv, trackSecond);
        int bestsme = GetMvpredVarCost(p, bestMv, out bestMvStats);
        int furtherSteps = cfg.NumSearchSteps - 1 - stepParam;
        while (n < furtherSteps)
        {
            ++n;
            DiamondSearchSad(startMv, startMvSad, p, stepParam + n, out int num00, out AomMv tmpBestMv, ref secondBestMv, trackSecond);
            int thissme = GetMvpredVarCost(p, tmpBestMv, out var tmpStats);
            if (thissme < bestsme)
            {
                bestsme = thissme;
                bestMv = tmpBestMv;
                bestMvStats = tmpStats;
            }
            if (num00 != 0) n += num00;
        }
        if (costList != null) CalcIntSadList(bestMv, p, costList, false);
        return bestsme;
    }

    // ---- exhaustive mesh search ----

    /// <summary>exhaustive_mesh_search (libaom's remainder loop covers end_col - c columns, one short).</summary>
    private static uint ExhaustiveMeshSearch(AomMv startMv, AomFullPelMsParams p, int range, int step, out AomMv bestMv,
        ref AomMv secondBestMv, bool trackSecond)
    {
        int colStep = step > 1 ? step : 4;
        startMv = ClampFullmv(startMv, p.MvLimits);
        bestMv = startMv;
        uint bestSad = GetMvpredSad(p, RefOffset(p.Ref, startMv.Row, startMv.Col));
        bestSad += (uint)MvsadErrCost(p, startMv);
        uint rawDummy = 0;
        int startRow = Math.Max(-range, p.MvLimits.RowMin - startMv.Row);
        int startCol = Math.Max(-range, p.MvLimits.ColMin - startMv.Col);
        int endRow = Math.Min(range, p.MvLimits.RowMax - startMv.Row);
        int endCol = Math.Min(range, p.MvLimits.ColMax - startMv.Col);
        for (int r = startRow; r <= endRow; r += step)
            for (int c = startCol; c <= endCol; c += colStep)
            {
                if (step > 1)
                {
                    var mv = new AomMv(startMv.Row + r, startMv.Col + c);
                    uint sad = GetMvpredSad(p, RefOffset(p.Ref, mv.Row, mv.Col));
                    UpdateMvsAndSad(p, sad, mv, ref bestSad, ref rawDummy, ref bestMv, ref secondBestMv, trackSecond);
                }
                else if (c + 3 <= endCol)
                {
                    for (int i = 0; i < 4; ++i)
                    {
                        var mv = new AomMv(startMv.Row + r, startMv.Col + c + i);
                        uint sad = GetMvpredSad(p, RefOffset(p.Ref, mv.Row, mv.Col));
                        if (sad < bestSad) UpdateMvsAndSad(p, sad, mv, ref bestSad, ref rawDummy, ref bestMv, ref secondBestMv, trackSecond);
                    }
                }
                else
                {
                    for (int i = 0; i < endCol - c; ++i)
                    {
                        var mv = new AomMv(startMv.Row + r, startMv.Col + c + i);
                        uint sad = GetMvpredSad(p, RefOffset(p.Ref, mv.Row, mv.Col));
                        UpdateMvsAndSad(p, sad, mv, ref bestSad, ref rawDummy, ref bestMv, ref secondBestMv, trackSecond);
                    }
                }
            }
        return bestSad;
    }

    /// <summary>full_pixel_exhaustive.</summary>
    private static int FullPixelExhaustive(AomMv startMv, AomFullPelMsParams p, AomMeshPattern[] meshPatterns, int[]? costList, out AomMv bestMv,
        out AomFullpelMvStats mvStats, ref AomMv secondBestMv, bool trackSecond)
    {
        const int kMinRange = 7, kMaxRange = 256, kMinInterval = 1;
        int interval = meshPatterns[0].interval, range = meshPatterns[0].range;
        bestMv = startMv;
        mvStats = default;
        if (range < kMinRange || range > kMaxRange || interval < kMinInterval || interval > range) return int.MaxValue;
        int baselineIntervalDivisor = range / interval;
        range = Math.Max(range, (5 * Math.Max(Math.Abs((int)bestMv.Row), Math.Abs((int)bestMv.Col))) / 4);
        range = Math.Min(range, kMaxRange);
        interval = Math.Max(interval, range / baselineIntervalDivisor);
        if (p.FineSearchInterval) interval = Math.Min(interval, 4);
        int bestsme = (int)ExhaustiveMeshSearch(bestMv, p, range, interval, out bestMv, ref secondBestMv, trackSecond);
        if (interval > kMinInterval && range > kMinRange)
            for (int i = 1; i < MAX_MESH_STEP; ++i)
            {
                bestsme = (int)ExhaustiveMeshSearch(bestMv, p, meshPatterns[i].range, meshPatterns[i].interval, out bestMv, ref secondBestMv, trackSecond);
                if (meshPatterns[i].interval == 1) break;
            }
        if (bestsme < int.MaxValue) bestsme = GetMvpredVarCost(p, bestMv, out mvStats);
        if (costList != null) CalcIntSadList(bestMv, p, costList, false);
        return bestsme;
    }

    /// <summary>av1_refining_search_8p_c.</summary>
    public static int RefiningSearch8p(AomFullPelMsParams p, AomMv startMv, out AomMv bestMv)
    {
        const int SEARCH_RANGE_8P = 3, SEARCH_GRID_STRIDE_8P = 2 * SEARCH_RANGE_8P + 1, SEARCH_GRID_CENTER_8P = SEARCH_RANGE_8P * SEARCH_GRID_STRIDE_8P + SEARCH_RANGE_8P;
        ReadOnlySpan<int> nr = stackalloc int[] { -1, 0, 0, 1, -1, 1, -1, 1 };
        ReadOnlySpan<int> nc = stackalloc int[] { 0, -1, 1, 0, -1, -1, 1, 1 };
        Span<byte> grid = stackalloc byte[SEARCH_GRID_STRIDE_8P * SEARCH_GRID_STRIDE_8P];
        grid.Clear();
        int gridCenter = SEARCH_GRID_CENTER_8P;
        bestMv = ClampFullmv(startMv, p.MvLimits);
        uint bestSad = GetMvpredCompoundSad(p, RefOffset(p.Ref, bestMv.Row, bestMv.Col));
        bestSad += (uint)MvsadErrCost(p, bestMv);
        grid[gridCenter] = 1;
        for (int i = 0; i < SEARCH_RANGE_8P; ++i)
        {
            int bestSite = -1;
            for (int j = 0; j < 8; ++j)
            {
                int gc = gridCenter + nr[j] * SEARCH_GRID_STRIDE_8P + nc[j];
                if (grid[gc] == 1) continue;
                var mv = new AomMv(bestMv.Row + nr[j], bestMv.Col + nc[j]);
                grid[gc] = 1;
                if (IsFullmvInRange(p.MvLimits, mv))
                {
                    uint sad = GetMvpredCompoundSad(p, RefOffset(p.Ref, mv.Row, mv.Col));
                    if (sad < bestSad)
                    {
                        sad += (uint)MvsadErrCost(p, mv);
                        if (sad < bestSad) { bestSad = sad; bestSite = j; }
                    }
                }
            }
            if (bestSite == -1) break;
            bestMv = new AomMv(bestMv.Row + nr[bestSite], bestMv.Col + nc[bestSite]);
            gridCenter += nr[bestSite] * SEARCH_GRID_STRIDE_8P + nc[bestSite];
        }
        return (int)bestSad;
    }

    /// <summary>av1_full_pixel_search (single reference, cost_list NULL, no second best).</summary>
    public static int FullPixelSearch(AomMv startMv, AomFullPelMsParams p, int stepParam, out AomMv bestMv)
    {
        AomMv dummy = default;
        return FullPixelSearch(startMv, p, stepParam, null, out bestMv, out _, ref dummy, false);
    }

    /// <summary>av1_full_pixel_search.</summary>
    public static int FullPixelSearch(AomMv startMv, AomFullPelMsParams p, int stepParam, int[]? costList, out AomMv bestMv,
        out AomFullpelMvStats bestMvStats, ref AomMv secondBestMv, bool trackSecond)
    {
        int r = FullPixelSearchCore(startMv, p, stepParam, costList, out bestMv, out bestMvStats, ref secondBestMv, trackSecond);
        AomTrace.Out?.Write($"fps start {startMv.Row} {startMv.Col} step {stepParam} meth {p.SearchMethod} lim {p.MvLimits.RowMin} {p.MvLimits.RowMax} {p.MvLimits.ColMin} {p.MvLimits.ColMax} -> {r} mv {bestMv.Row} {bestMv.Col} dist {(uint)bestMvStats.Distortion} sse {(uint)bestMvStats.Sse} second {(trackSecond ? secondBestMv.Row : -9999)} {(trackSecond ? secondBestMv.Col : -9999)}" + (char)10);
        return r;
    }

    private static int FullPixelSearchCore(AomMv startMv, AomFullPelMsParams p, int stepParam, int[]? costList, out AomMv bestMv,
        out AomFullpelMvStats bestMvStats, ref AomMv secondBestMv, bool trackSecond)
    {
        int bsize = p.Bsize;
        int searchMethod = p.SearchMethod;
        bool runMeshSearch = p.RunMeshSearch;
        int var;
        if (trackSecond) secondBestMv = AomMv.Invalid;
        if (costList != null) costList[0] = costList[1] = costList[2] = costList[3] = costList[4] = int.MaxValue;
        switch (searchMethod)
        {
            case FAST_BIGDIA: var = PatternSearch(startMv, p, Math.Max(MAX_MVSEARCH_STEPS - 3, stepParam), false, costList, out bestMv, out bestMvStats); break;
            case VFAST_DIAMOND: var = PatternSearch(startMv, p, Math.Max(MAX_MVSEARCH_STEPS - 1, stepParam), false, costList, out bestMv, out bestMvStats); break;
            case FAST_DIAMOND: var = PatternSearch(startMv, p, Math.Max(MAX_MVSEARCH_STEPS - 2, stepParam), false, costList, out bestMv, out bestMvStats); break;
            case HEX: var = PatternSearch(startMv, p, stepParam, true, costList, out bestMv, out bestMvStats); break;
            case BIGDIA: var = PatternSearch(startMv, p, stepParam, true, costList, out bestMv, out bestMvStats); break;
            case NSTEP: case NSTEP_8PT: case DIAMOND: case CLAMPED_DIAMOND:
                var = FullPixelDiamond(startMv, p, stepParam, costList, out bestMv, out bestMvStats, ref secondBestMv, trackSecond); break;
            default: throw new ArgumentOutOfRangeException(nameof(p), "invalid search method");
        }

        if (!runMeshSearch && (searchMethod == NSTEP || searchMethod == NSTEP_8PT) && !p.HasSecondPred)
        {
            int exhaustiveThr = p.ForceMeshThresh;
            exhaustiveThr >>= 10 - (MiSizeWideLog2[bsize] + MiSizeHighLog2[bsize]);
            if (var > exhaustiveThr) runMeshSearch = true;
        }
        if (!p.IsIntraMode && p.PruneMeshSearch)
        {
            int fullPelMvDiff = Math.Max(Math.Abs(startMv.Row - bestMv.Row), Math.Abs(startMv.Col - bestMv.Col));
            if (fullPelMvDiff <= p.MeshSearchMvDiffThreshold) runMeshSearch = false;
        }
        if (p.SkipSad)
        {
            // skipping rows: redo the search with the full SAD when the skip-row SAD is far off at the best mv
            int bestOff = RefOffset(p.Ref, bestMv.Row, bestMv.Col);
            int sad = (int)SdfAt(p, bestOff, false);
            int skipSad = (int)SdfAt(p, bestOff, true);
            int kSadThresh = 1 << (MiSizeWideLog2[bsize] + MiSizeHighLog2[bsize]);
            if (sad > kSadThresh && Math.Abs(skipSad - sad) * 10 >= Math.Max(sad, 1) * 9)
            {
                p.SkipSad = false;
                try { return FullPixelSearch(startMv, p, stepParam, costList, out bestMv, out bestMvStats, ref secondBestMv, trackSecond); }
                finally { p.SkipSad = true; }
            }
        }
        if (runMeshSearch)
        {
            var meshPatterns = p.MeshPatterns[p.IsIntraMode ? 1 : 0];
            int varEx = FullPixelExhaustive(bestMv, p, meshPatterns, costList, out AomMv tmpMvEx, out var tmpStats, ref secondBestMv, trackSecond);
            if (varEx < var)
            {
                var = varEx;
                bestMvStats = tmpStats;
                bestMv = tmpMvEx;
            }
        }
        return var;
    }

    /// <summary>av1_intrabc_hash_search.</summary>
    public static int IntrabcHashSearch(AomComp cpi, AomMacroblockD xd, AomFullPelMsParams p, AomIntrabcHashInfo? info, out AomMv bestMv)
    {
        bestMv = default;
        if (!cpi.UseHashMe || info == null) return int.MaxValue;
        int bsize = p.Bsize;
        int blockWidth = BlockSizeWide[bsize], blockHeight = BlockSizeHigh[bsize];
        if (blockWidth != blockHeight) return int.MaxValue;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        int xPos = miCol * 4, yPos = miRow * 4;
        int bestHashCost = int.MaxValue;
        AomHashMotion.GetBlockHashValue(info, p.Src.Buf, p.Src.Buf16, p.Src.Offset, p.Src.Stride, blockWidth, out uint hashValue1, out uint hashValue2);
        int count = AomHashMotion.Count(info, hashValue1);
        if (count <= 1) return int.MaxValue;
        if (cpi.Sf.mv_sf.prune_intrabc_candidate_block_hash_search != 0) count = Math.Min(64, count);
        var bucket = info.LookupTable[hashValue1]!;
        for (int i = 0; i < count; i++)
        {
            var refBlockHash = bucket[i];
            if (hashValue2 != refBlockHash.HashValue2) continue;
            var dv = new AomMv((refBlockHash.Y - yPos) * 8, (refBlockHash.X - xPos) * 8);
            if (!AomMvRef.IsDvValid(dv, cpi.Cm, xd, miRow, miCol, bsize, cpi.Cm.MibSizeLog2)) continue;
            var hashMv = new AomMv(refBlockHash.Y - yPos, refBlockHash.X - xPos);
            if (!IsFullmvInRange(p.MvLimits, hashMv)) continue;
            int refCost = GetMvpredVarCost(p, hashMv);
            if (refCost < bestHashCost)
            {
                bestHashCost = refCost;
                bestMv = hashMv;
            }
        }
        return bestHashCost;
    }
}
