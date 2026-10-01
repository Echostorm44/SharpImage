using System;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Formats.Av1;

internal sealed partial class AomComp
{
    // oxcf.kf_cfg.enable_intrabc
    public bool EnableIntrabcCfg = true;
    // the frame's intrabc hash table (av1_use_hash_me), full-pel search sites (search_site_cfg[SS_CFG_LOOKAHEAD]) and
    // mv_search_params.mv_step_param
    public AomIntrabcHashInfo? IntrabcHash;
    public AomSearchSiteConfig[] SearchSites = Array.Empty<AomSearchSiteConfig>();
    public int MvStepParam;
    /// <summary>cpi->intrabc_used: some block of the frame is coded with intrabc (the final allow_intrabc).</summary>
    public bool IntrabcUsed;
    /// <summary>cpi->mbmi_ext_info.frame_base (per mi; allocated when intrabc is allowed, entries on first write): the
    /// bitstream writer's MB_MODE_INFO_EXT_FRAME of each block (an intrabc block's DV reference is
    /// RefMvStack[0].ThisMv).</summary>
    public AomMbmiExtFrame?[]? MbmiExtFrameBase;

    /// <summary>av1_allow_intrabc.</summary>
    public bool AllowIntrabcNow => FrameIsIntraOnly && AllowScreenContentTools && AllowIntrabc;
    /// <summary>av1_use_hash_me.</summary>
    public bool UseHashMe => AllowScreenContentTools && AllowIntrabc && FrameIsIntraOnly;

    /// <summary>The frame-level MB_MODE_INFO_EXT_FRAME of the block at (miRow, miCol) (null when intrabc was not
    /// allowed in the frame).</summary>
    public AomMbmiExtFrame? MbmiExtFrameAt(int miRow, int miCol) => MbmiExtFrameBase?[miRow * Cm.MiStride + miCol];
}

internal sealed partial class AomMacroblock
{
    // x->mbmi_ext, x->dv_costs, x->sadperbit
    public readonly AomMbmiExt MbmiExt = new();
    public AomDvCosts? DvCosts;
    public int SadPerBit;
}

// Port of libaom 3.14.1's intrabc path of the all-intra encoder: rdopt.c's rd_pick_intrabc_mode_sb and, for the
// final encode of intrabc blocks, partition_search.c's encode_superblock inter branch (av1_encode_sb /
// encode_block_inter / encode_block from encodemb.c, av1_tokenize_sb_vartx from tokenize.c,
// tx_partition_count_update / update_txfm_count / tx_partition_set_contexts / set_txfm_context).
internal static partial class AomEncodeFrame
{
    private const int IBC_MOTION_ABOVE = 0, IBC_MOTION_LEFT = 1, IBC_MOTION_DIRECTIONS = 2;
    private const int BILINEAR = 3;   // InterpFilter

    /// <summary>av1_set_sad_per_bit (8-bit): sad_per_bit_lut_8[qindex] = (int)(0.0418 q + 2.4107), q = ac_q / 4.</summary>
    internal static int SadPerBit(int qindex)
    {
        double q = Av1Tables.DequantTable[0, qindex, 1] / 4.0;   // av1_convert_qindex_to_q
        return (int)(0.0418 * q + 2.4107);
    }

    /// <summary>rd_pick_intrabc_mode_sb: the best intrabc DV of the block (above and left search areas), when it beats
    /// bestRd; the block's mode info and rd stats are then the intrabc ones. Returns the best RD cost.</summary>
    internal static long RdPickIntrabcModeSb(AomComp cpi, AomMacroblock x, AomPickModeContext ctx, ref AomRdStats rdStats, int bsize, long bestRd)
    {
        var cm = cpi.Cm;
        var sf = cpi.Sf;
        if (!cpi.AllowIntrabcNow || !cpi.EnableIntrabcCfg || sf.mv_sf.use_intrabc == 0 || sf.rt_sf.use_nonrd_pick_mode != 0) return long.MaxValue;
        if (sf.mv_sf.intrabc_search_level >= 1 && bsize != BLOCK_4X4 && bsize != BLOCK_8X8 && bsize != BLOCK_16X16) return long.MaxValue;
        int numPlanes = cm.NumPlanes;
        var xd = x.E;
        var mbmi = xd.Mi0;
        int miRow = xd.MiRow, miCol = xd.MiCol;
        int w = BlockSizeWide[bsize], h = BlockSizeHigh[bsize];
        int sbRow = miRow >> cm.MibSizeLog2, sbCol = miCol >> cm.MibSizeLog2;

        var ext = x.MbmiExt;
        AomMvRef.FindMvRefsIntra(cm, xd, ext);
        AomMvRef.CopyUsableRefMvStackAndWeight(xd, ext);
        AomMvRef.FindBestRefMvsFromStack(false, ext, out AomMv nearestmv, out AomMv nearmv);
        if (nearestmv.AsInt == AomMv.Invalid.AsInt) nearestmv = default;
        if (nearmv.AsInt == AomMv.Invalid.AsInt) nearmv = default;
        AomMv dvRef = nearestmv.AsInt == 0 ? nearmv : nearestmv;
        if (dvRef.AsInt == 0) dvRef = AomMvRef.FindRefDv(xd, cm.MibSize, miRow);
        ext.RefMvStack[0].ThisMv = dvRef;
        AomTrace.Out?.Write($"ibc {miRow} {miCol} bs {bsize} cnt {ext.RefMvCount} nearest {nearestmv} near {nearmv} dvref {dvRef}\n");
        if (AomTrace.Out != null && Environment.GetEnvironmentVariable("AOMORACLE_SBHASH") != null) TraceSbPixels(cpi, sbRow, sbCol);

        // av1_setup_pred_block(xd, ..., xd->cur_buf, ...): in libaom's encoder xd->cur_buf is the SOURCE frame
        // (av1_setup_src_planes sets it), so the DV search (hash verification, full-pel search) measures the source
        // block against source samples, not the reconstruction; the prediction itself (build_inter_predictors) reads
        // the reconstruction through dst
        for (int i = 0; i < numPlanes; ++i) xd.Plane[i].Pre0 = x.Plane[i].Src;

        var bestMbmi = mbmi.Clone();
        var bestRdstats = rdStats;
        Span<byte> bestTxTypeMap = stackalloc byte[AomMacroblockD.MaxMibSize * AomMacroblockD.MaxMibSize];
        xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, ctx.NumFourByFourBlk).CopyTo(bestTxTypeMap);

        int searchMethod = AomMcomp.GetDefaultMvSearchMethod(x, sf.mv_sf, bsize);
        var startMv = dvRef.ToFullMv();
        var fullms = AomMcomp.MakeDefaultFullpelMsParams(cpi, x, bsize, dvRef, cpi.SearchSites, searchMethod, false, default);
        var dvCosts = x.DvCosts!;
        AomMcomp.SetMsToIntraMode(fullms, dvCosts);

        int maxDir = sf.mv_sf.intrabc_search_level != 0 ? IBC_MOTION_LEFT : IBC_MOTION_DIRECTIONS;
        for (int dir = IBC_MOTION_ABOVE; dir < maxDir; ++dir)
        {
            if (dir == IBC_MOTION_ABOVE)
            {
                fullms.MvLimits.ColMin = (xd.TileMiColStart - miCol) * 4;
                fullms.MvLimits.ColMax = (xd.TileMiColEnd - miCol) * 4 - w;
                fullms.MvLimits.RowMin = (xd.TileMiRowStart - miRow) * 4;
                fullms.MvLimits.RowMax = (sbRow * cm.MibSize - miRow) * 4 - h;
            }
            else
            {
                fullms.MvLimits.ColMin = (xd.TileMiColStart - miCol) * 4;
                fullms.MvLimits.ColMax = (sbCol * cm.MibSize - miCol) * 4 - w;
                fullms.MvLimits.RowMin = (xd.TileMiRowStart - miRow) * 4;
                int bottomCodedMiEdge = Math.Min((sbRow + 1) * cm.MibSize, xd.TileMiRowEnd);
                fullms.MvLimits.RowMax = (bottomCodedMiEdge - miRow) * 4 - h;
            }
            AomMcomp.SetMvSearchRange(ref fullms.MvLimits, dvRef);
            AomTrace.Out?.Write($"ibcdir {dir} lim {fullms.MvLimits}\n");
            if (fullms.MvLimits.ColMax < fullms.MvLimits.ColMin || fullms.MvLimits.RowMax < fullms.MvLimits.RowMin) continue;

            int stepParam = cpi.MvStepParam;
            AomMv bestMv = default;
            int bestsme = int.MaxValue;
            // hash search first
            if (sf.mv_sf.hash_max_8x8_intrabc_blocks == 0 || bsize <= BLOCK_8X8)
            {
                bestsme = AomMcomp.IntrabcHashSearch(cpi, xd, fullms, cpi.IntrabcHash, out bestMv);
                AomTrace.Out?.Write($"ibchash {bestsme} {bestMv}\n");
            }
            // with intrabc_search_level, a hash match skips the pixel search
            if (bestsme == int.MaxValue || sf.mv_sf.intrabc_search_level == 0)
            {
                int pixelsme = AomMcomp.FullPixelSearch(startMv, fullms, stepParam, out AomMv bestPixelMv);
                AomTrace.Out?.Write($"ibcpix {pixelsme} {bestPixelMv}\n");
                if (pixelsme < bestsme)
                {
                    bestsme = pixelsme;
                    bestMv = bestPixelMv;
                }
            }
            if (bestsme == int.MaxValue) continue;
            var dv = bestMv.ToMv();
            if (!AomMcomp.IsFullmvInRange(fullms.MvLimits, dv.ToFullMv())) continue;
            if (!AomMvRef.IsDvValid(dv, cm, xd, miRow, miCol, bsize, cm.MibSizeLog2)) continue;

            mbmi.Palette.PaletteSize0 = 0;
            mbmi.Palette.PaletteSize1 = 0;
            Array.Clear(mbmi.Palette.PaletteColors);
            mbmi.UseFilterIntra = 0;
            mbmi.UseIntrabc = 1;
            mbmi.Mode = DC_PRED;
            mbmi.UvMode = UV_DC_PRED;
            mbmi.MotionMode = 0;   // SIMPLE_TRANSLATION
            mbmi.Mv0 = dv;
            mbmi.InterpFilters = (BILINEAR << 16) | BILINEAR;   // av1_broadcast_interp_filter
            mbmi.SkipTxfm = 0;
            AomReconInter.BuildIntrabcPredictor(cm, xd, miRow, miCol, 0, numPlanes - 1);

            int rateMv = AomMvCost.MvBitCost(dv, dvRef, dvCosts.JointMv, dvCosts.DvCosts, AomMvCost.MV_COST_WEIGHT_SUB);
            int rateMode = x.ModeCosts.IntrabcCost[1];
            AomTrace.Out?.Write($"ibccand {dv} ratemv {rateMv}\n");
            AomRdStats rdStatsYuv = default, rdStatsY = default, rdStatsUv = default;
            bool ok = AomTxSearch.TxfmSearch(cpi, x, bsize, ref rdStatsYuv, ref rdStatsY, ref rdStatsUv, rateMode + rateMv, long.MaxValue);
            AomTrace.Out?.Write($"ibctx {(ok ? 1 : 0)} y {rdStatsY.Rate} {rdStatsY.Dist} uv {rdStatsUv.Rate} {rdStatsUv.Dist} rate {rdStatsYuv.Rate} dist {rdStatsYuv.Dist} skip {mbmi.SkipTxfm} tx {mbmi.TxSize}\n");
            if (!ok) continue;
            rdStatsYuv.Rdcost = AomRd.RdCost(x.Rdmult, rdStatsYuv.Rate, rdStatsYuv.Dist);
            if (rdStatsYuv.Rdcost < bestRd)
            {
                bestRd = rdStatsYuv.Rdcost;
                bestMbmi.CopyFrom(mbmi);
                bestRdstats = rdStatsYuv;
                xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset, xd.Height * xd.Width).CopyTo(bestTxTypeMap);
            }
        }
        mbmi.CopyFrom(bestMbmi);
        rdStats = bestRdstats;
        bestTxTypeMap.Slice(0, ctx.NumFourByFourBlk).CopyTo(xd.TxTypeMap.AsSpan(xd.TxTypeMapOffset));
        AomTrace.Out?.Write($"ibcend {bestRd} ibc {mbmi.UseIntrabc}\n");
        return bestRd;
    }

    // debug trace: the current superblock's luma reconstruction (hash, optionally the samples)
    private static void TraceSbPixels(AomComp cpi, int sbRow, int sbCol)
    {
        var cm = cpi.Cm;
        var cur = cm.CurFrame;
        int sbpx = cm.MibSize * 4;
        int o = cur.Offsets[0] + sbRow * sbpx * cur.Strides[0] + sbCol * sbpx;
        ulong hh = 1469598103934665603UL;
        for (int r = 0; r < sbpx; r++)
            for (int c = 0; c < sbpx; c++) hh = (hh ^ cur.Buffers[0][o + r * cur.Strides[0] + c]) * 1099511628211UL;
        AomTrace.Out!.Write($"sbhash {hh:x16}\n");
        if (Environment.GetEnvironmentVariable("AOMORACLE_SBDUMP") != null)
        {
            var sb = new System.Text.StringBuilder("sbpx");
            for (int r = 0; r < sbpx; r++)
                for (int c = 0; c < sbpx; c++) sb.Append(' ').Append(cur.Buffers[0][o + r * cur.Strides[0] + c]);
            AomTrace.Out.Write(sb.Append('\n').ToString());
        }
    }

    // ---- final encode of intrabc (inter) blocks ----

    /// <summary>encode_block (encodemb.c) for an inter block's tx block (skip_mode is off on key frames).</summary>
    private static void EncodeBlockInterTx(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize,
        byte[] ta, byte[] tl, int dryRun, int enableOptimizeB)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var p = x.Plane[plane];
        var pd = xd.Plane[plane];
        int dstStride = pd.Dst.Stride;
        int dstOff = pd.Dst.Offset + ((blkRow * dstStride + blkCol) << 2);
        int txType = AomEncodeMb.GetTxType(xd, pd.PlaneType, blkRow, blkCol, txSize, cpi.ReducedTxSetUsed != 0);
        bool useTrellis = AomTxSearch.IsTrellisUsed(enableOptimizeB, dryRun);
        int quantIdx = useTrellis ? AomXformQuant.Fp : AomXformQuant.B;   // USE_B_QUANT_NO_TRELLIS
        var qp = AomEncodeMb.SetupQuant(txSize, useTrellis, quantIdx, cpi.QuantBAdapt);
        AomEncodeMb.SetupQmatrix(x, plane, txSize, txType, ref qp);
        AomEncodeMb.Xform(x, plane, block, blkRow, blkCol, planeBsize, txSize, txType);
        AomEncodeMb.Quant(x, plane, block, txSize, txType, qp);
        if (useTrellis)
        {
            var txbCtx = AomTxb.TxbCtx(planeBsize, txSize, plane, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
            AomEncodeMb.OptimizeB(cpi, x, plane, block, txSize, txType, txbCtx, out _);
        }
        AomEncodeMb.SetTxbContext(x, plane, block, txSize, ta.AsSpan(blkCol), tl.AsSpan(blkRow));
        int eob = p.Eobs[block];
        if (eob != 0)
        {
            mbmi.SkipTxfm = 0;
            AomEncodeMb.InverseTransformBlock(p.Dqcoeff, AomEncodeMb.BlockOffset(block), txType, txSize, pd.Dst.Buf, dstOff, dstStride, eob,
                xd.Lossless[mbmi.SegmentId] != 0);
        }
        else mbmi.SkipTxfm &= 1;
        if (eob == 0 && plane == 0) AomEncodeMb.UpdateTxkArray(xd, blkRow, blkCol, txSize, DCT_DCT);
    }

    /// <summary>encode_block_inter: the var-tx tree of a luma tx block, chroma at its single size.</summary>
    private static void EncodeBlockInter(AomComp cpi, AomMacroblock x, int plane, int block, int blkRow, int blkCol, int planeBsize, int txSize,
        byte[] ta, byte[] tl, int dryRun, int enableOptimizeB)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var pd = xd.Plane[plane];
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane);
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        int planeTxSize = plane != 0 ? AomEncodeMb.MaxUvTxsize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY)
            : mbmi.InterTxSize[AomTxSearch.GetTxbSizeIndex(planeBsize, blkRow, blkCol)];
        if (txSize == planeTxSize || plane != 0)
        {
            EncodeBlockInterTx(cpi, x, plane, block, blkRow, blkCol, planeBsize, txSize, ta, tl, dryRun, enableOptimizeB);
            return;
        }
        int subTxs = SubTxSizeMap[txSize];
        int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
        int step = bsh * bsw;
        int rowEnd = Math.Min(TxSizeHighUnit[txSize], maxBlocksHigh - blkRow);
        int colEnd = Math.Min(TxSizeWideUnit[txSize], maxBlocksWide - blkCol);
        for (int row = 0; row < rowEnd; row += bsh)
            for (int col = 0; col < colEnd; col += bsw)
            {
                EncodeBlockInter(cpi, x, plane, block, blkRow + row, blkCol + col, planeBsize, subTxs, ta, tl, dryRun, enableOptimizeB);
                block += step;
            }
    }

    [ThreadStatic] private static byte[]? t_ita, t_itl;

    /// <summary>av1_encode_sb: the residual coding and reconstruction of an inter block.</summary>
    private static void EncodeSbInter(AomComp cpi, AomMacroblock x, int bsize, int dryRun)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        mbmi.SkipTxfm = 1;
        if (x.TxfmSkip != 0) return;
        int enableOptimizeB = cpi.OptimizeSegArr[mbmi.SegmentId];
        int numPlanes = cpi.Cm.NumPlanes;
        var ta = t_ita ??= new byte[AomMacroblockD.MaxMibSize];
        var tl = t_itl ??= new byte[AomMacroblockD.MaxMibSize];
        for (int plane = 0; plane < numPlanes; ++plane)
        {
            var pd = xd.Plane[plane];
            if (plane != 0 && !xd.IsChromaRef) break;
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            int miWidth = MiSizeWide[planeBsize], miHeight = MiSizeHigh[planeBsize];
            int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, planeBsize, plane);
            int txbSize = TxsizeToBsize[maxTxSize];
            int bw = MiSizeWide[txbSize], bh = MiSizeHigh[txbSize];
            int block = 0;
            int step = TxSizeWideUnit[maxTxSize] * TxSizeHighUnit[maxTxSize];
            AomTxSearch.GetEntropyContexts(planeBsize, pd, ta, tl);
            AomEncodeMb.SubtractPlane(x, planeBsize, plane);
            int maxUnitBsize = AomEncodeMb.PlaneBlockSize(BLOCK_64X64, pd.SubsamplingX, pd.SubsamplingY);
            int muBlocksWide = Math.Min(miWidth, MiSizeWide[maxUnitBsize]), muBlocksHigh = Math.Min(miHeight, MiSizeHigh[maxUnitBsize]);
            for (int idy = 0; idy < miHeight; idy += muBlocksHigh)
                for (int idx = 0; idx < miWidth; idx += muBlocksWide)
                {
                    int unitHeight = Math.Min(muBlocksHigh + idy, miHeight), unitWidth = Math.Min(muBlocksWide + idx, miWidth);
                    for (int blkRow = idy; blkRow < unitHeight; blkRow += bh)
                        for (int blkCol = idx; blkCol < unitWidth; blkCol += bw)
                        {
                            EncodeBlockInter(cpi, x, plane, block, blkRow, blkCol, planeBsize, maxTxSize, ta, tl, dryRun, enableOptimizeB);
                            block += step;
                        }
                }
        }
    }

    /// <summary>tokenize_vartx.</summary>
    private static void TokenizeVartx(AomComp cpi, AomMacroblock x, int txSize, int planeBsize, int blkRow, int blkCol, int block, int plane,
        int dryRun, bool allowUpdateCdf)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        var pd = xd.Plane[plane];
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, planeBsize, plane), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, planeBsize, plane);
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        int planeTxSize = plane != 0 ? AomEncodeMb.MaxUvTxsize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY)
            : mbmi.InterTxSize[AomTxSearch.GetTxbSizeIndex(planeBsize, blkRow, blkCol)];
        if (txSize == planeTxSize || plane != 0)
        {
            int pb = AomEncodeMb.PlaneBlockSize(mbmi.Bsize, pd.SubsamplingX, pd.SubsamplingY);
            UpdateAndRecordTxbContext(cpi, x, plane, block, blkRow, blkCol, pb, txSize, dryRun, allowUpdateCdf);
            return;
        }
        int subTxs = SubTxSizeMap[txSize];
        int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
        int step = bsw * bsh;
        int rowEnd = Math.Min(TxSizeHighUnit[txSize], maxBlocksHigh - blkRow);
        int colEnd = Math.Min(TxSizeWideUnit[txSize], maxBlocksWide - blkCol);
        for (int row = 0; row < rowEnd; row += bsh)
            for (int col = 0; col < colEnd; col += bsw)
            {
                TokenizeVartx(cpi, x, subTxs, planeBsize, blkRow + row, blkCol + col, block, plane, dryRun, allowUpdateCdf);
                block += step;
            }
    }

    /// <summary>av1_tokenize_sb_vartx (av1_update_and_record_txb_context / av1_record_txb_context per tx block).</summary>
    private static void TokenizeSbVartx(AomComp cpi, AomMacroblock x, int dryRun, int bsize, bool allowUpdateCdf)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        if (xd.MiRow >= cm.MiRows || xd.MiCol >= cm.MiCols) return;
        var mbmi = xd.Mi0;
        if (mbmi.SkipTxfm != 0)
        {
            ResetEntropyContext(xd, bsize, cm.NumPlanes);
            return;
        }
        for (int plane = 0; plane < cm.NumPlanes; ++plane)
        {
            if (plane != 0 && !xd.IsChromaRef) break;
            var pd = xd.Plane[plane];
            int planeBsize = AomEncodeMb.PlaneBlockSize(bsize, pd.SubsamplingX, pd.SubsamplingY);
            int miWidth = MiSizeWide[planeBsize], miHeight = MiSizeHigh[planeBsize];
            int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, planeBsize, plane);
            int txbSize = TxsizeToBsize[maxTxSize];
            int bw = MiSizeWide[txbSize], bh = MiSizeHigh[txbSize];
            int block = 0;
            int step = TxSizeWideUnit[maxTxSize] * TxSizeHighUnit[maxTxSize];
            int maxUnitBsize = AomEncodeMb.PlaneBlockSize(BLOCK_64X64, pd.SubsamplingX, pd.SubsamplingY);
            int muBlocksWide = Math.Min(miWidth, MiSizeWide[maxUnitBsize]), muBlocksHigh = Math.Min(miHeight, MiSizeHigh[maxUnitBsize]);
            for (int idy = 0; idy < miHeight; idy += muBlocksHigh)
                for (int idx = 0; idx < miWidth; idx += muBlocksWide)
                {
                    int unitHeight = Math.Min(muBlocksHigh + idy, miHeight), unitWidth = Math.Min(muBlocksWide + idx, miWidth);
                    for (int blkRow = idy; blkRow < unitHeight; blkRow += bh)
                        for (int blkCol = idx; blkCol < unitWidth; blkCol += bw)
                        {
                            TokenizeVartx(cpi, x, maxTxSize, planeBsize, blkRow, blkCol, block, plane, dryRun, allowUpdateCdf);
                            block += step;
                        }
                }
        }
    }

    /// <summary>update_txfm_count.</summary>
    private static void UpdateTxfmCount(AomComp cpi, AomMacroblock x, int txSize, int depth, int blkRow, int blkCol, bool allowUpdateCdf)
    {
        var xd = x.E;
        var mbmi = xd.Mi0;
        int bsize = mbmi.Bsize;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bsize, 0);
        var above = xd.AboveTxfmContext;
        var left = xd.LeftTxfmContextBuffer;
        int aOff = xd.AboveTxfmContextOffset + blkCol, lOff = xd.LeftTxfmContextOffset + blkRow;
        int ctx = AomTxSearch.TxfmPartitionContext(above[aOff], left[lOff], mbmi.Bsize, txSize);
        int txbSizeIndex = AomTxSearch.GetTxbSizeIndex(bsize, blkRow, blkCol);
        int planeTxSize = mbmi.InterTxSize[txbSizeIndex];
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        if (depth == 2)   // MAX_VARTX_DEPTH
        {
            mbmi.TxSize = txSize;
            AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, txSize, txSize);
            return;
        }
        if (txSize == planeTxSize)
        {
            if (allowUpdateCdf) AomCdf.Update(x.TileCtx.Mode.Txpart[ctx], 0, 2);
            mbmi.TxSize = txSize;
            AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, txSize, txSize);
        }
        else
        {
            int subTxs = SubTxSizeMap[txSize];
            int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
            if (allowUpdateCdf) AomCdf.Update(x.TileCtx.Mode.Txpart[ctx], 1, 2);
            System.Threading.Interlocked.Increment(ref cpi.TxbSplitCount);   // x->txfm_search_info.txb_split_count (summed over the threads)
            if (subTxs == TX_4X4)
            {
                mbmi.InterTxSize[txbSizeIndex] = TX_4X4;
                mbmi.TxSize = TX_4X4;
                AomTxSearch.TxfmPartitionUpdate(above, aOff, left, lOff, TX_4X4, txSize);
                return;
            }
            for (int row = 0; row < TxSizeHighUnit[txSize]; row += bsh)
                for (int col = 0; col < TxSizeWideUnit[txSize]; col += bsw)
                    UpdateTxfmCount(cpi, x, subTxs, depth + 1, blkRow + row, blkCol + col, allowUpdateCdf);
        }
    }

    /// <summary>tx_partition_count_update.</summary>
    private static void TxPartitionCountUpdate(AomComp cpi, AomMacroblock x, int planeBsize, bool allowUpdateCdf)
    {
        var cm = cpi.Cm;
        var xd = x.E;
        int miWidth = MiSizeWide[planeBsize], miHeight = MiSizeHigh[planeBsize];
        int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, planeBsize, 0);
        int bh = TxSizeHighUnit[maxTxSize], bw = TxSizeWideUnit[maxTxSize];
        xd.AboveTxfmContext = cm.AboveTxfm[xd.TileRow];
        xd.AboveTxfmContextOffset = xd.MiCol;
        xd.LeftTxfmContextOffset = xd.MiRow & MAX_MIB_MASK;
        for (int idy = 0; idy < miHeight; idy += bh)
            for (int idx = 0; idx < miWidth; idx += bw)
                UpdateTxfmCount(cpi, x, maxTxSize, 0, idy, idx, allowUpdateCdf);
    }

    /// <summary>set_txfm_context.</summary>
    private static void SetTxfmContext(AomMacroblockD xd, int txSize, int blkRow, int blkCol)
    {
        var mbmi = xd.Mi0;
        int bsize = mbmi.Bsize;
        int maxBlocksHigh = AomEncodeMb.MaxBlockHigh(xd, bsize, 0), maxBlocksWide = AomEncodeMb.MaxBlockWide(xd, bsize, 0);
        int txbSizeIndex = AomTxSearch.GetTxbSizeIndex(bsize, blkRow, blkCol);
        int planeTxSize = mbmi.InterTxSize[txbSizeIndex];
        if (blkRow >= maxBlocksHigh || blkCol >= maxBlocksWide) return;
        int aOff = xd.AboveTxfmContextOffset + blkCol, lOff = xd.LeftTxfmContextOffset + blkRow;
        if (txSize == planeTxSize)
        {
            mbmi.TxSize = txSize;
            AomTxSearch.TxfmPartitionUpdate(xd.AboveTxfmContext, aOff, xd.LeftTxfmContextBuffer, lOff, txSize, txSize);
        }
        else
        {
            if (txSize == TX_8X8)
            {
                mbmi.InterTxSize[txbSizeIndex] = TX_4X4;
                mbmi.TxSize = TX_4X4;
                AomTxSearch.TxfmPartitionUpdate(xd.AboveTxfmContext, aOff, xd.LeftTxfmContextBuffer, lOff, TX_4X4, txSize);
                return;
            }
            int subTxs = SubTxSizeMap[txSize];
            int bsw = TxSizeWideUnit[subTxs], bsh = TxSizeHighUnit[subTxs];
            int rowEnd = Math.Min(TxSizeHighUnit[txSize], maxBlocksHigh - blkRow);
            int colEnd = Math.Min(TxSizeWideUnit[txSize], maxBlocksWide - blkCol);
            for (int row = 0; row < rowEnd; row += bsh)
                for (int col = 0; col < colEnd; col += bsw)
                    SetTxfmContext(xd, subTxs, blkRow + row, blkCol + col);
        }
    }

    /// <summary>tx_partition_set_contexts.</summary>
    private static void TxPartitionSetContexts(AomCommon cm, AomMacroblockD xd, int planeBsize)
    {
        int miWidth = MiSizeWide[planeBsize], miHeight = MiSizeHigh[planeBsize];
        int maxTxSize = AomTxSearch.GetVartxMaxTxsize(xd, planeBsize, 0);
        int bh = TxSizeHighUnit[maxTxSize], bw = TxSizeWideUnit[maxTxSize];
        xd.AboveTxfmContext = cm.AboveTxfm[xd.TileRow];
        xd.AboveTxfmContextOffset = xd.MiCol;
        xd.LeftTxfmContextOffset = xd.MiRow & MAX_MIB_MASK;
        for (int idy = 0; idy < miHeight; idy += bh)
            for (int idx = 0; idx < miWidth; idx += bw)
                SetTxfmContext(xd, maxTxSize, idy, idx);
    }
}
