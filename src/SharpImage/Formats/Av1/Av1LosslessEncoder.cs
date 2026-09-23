// AV1 lossless intra coding (base_q_idx = 0 ⇒ CodedLossless): every transform is a 4x4 Walsh-Hadamard (WHT_WHT,
// implied — no tx-type symbol), TX_MODE is ONLY_4X4, and the loop filter / CDEF / restoration are off. Because the
// reconstruction equals the source exactly, predictions are taken straight from the (padded) source planes; the
// decoder's own availability rules (tile-relative edges, per-transform top-right / bottom-left) decide which of those
// pixels a prediction may read. Superblocks are split down to 8x8 blocks; each picks the luma / chroma intra mode
// whose WHT levels are smallest. Chroma may be 4:2:0, 4:2:2 or 4:4:4 (or absent for monochrome / alpha).
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Av1;

internal static class Av1LosslessEncoder
{
    private static readonly Av1IntraPredMode[] Modes =
    [
        Av1IntraPredMode.Dc, Av1IntraPredMode.Vertical, Av1IntraPredMode.Horizontal, Av1IntraPredMode.Paeth,
        Av1IntraPredMode.Smooth, Av1IntraPredMode.SmoothV, Av1IntraPredMode.SmoothH, Av1IntraPredMode.DiagDownLeft,
        Av1IntraPredMode.DiagDownRight, Av1IntraPredMode.VerticalRight, Av1IntraPredMode.HorizontalDown, Av1IntraPredMode.HorizontalUp, Av1IntraPredMode.VerticalLeft,
    ];

    private const int Tx4 = 0;                       // TX_4X4
    private const int Bs8 = (int)Av1BlockSize.Bs8x8;
    private const int EdgeFilterBit = 1 << 10, SmoothBit = 1 << 9;

    private sealed class Ctx
    {
        public Av1MsacWriter Msac = new();
        public Av1CdfContext Cdf = new();
        public ushort[] Y = null!, U = null!, V = null!;
        public int W, Cw, Bw4, Bh4, SsX, SsY, Bd;
        public bool Chroma, EdgeFilter;
        public Av1PixelLayout Layout;
        public int TileX4, TileY4, TileEndX4, TileEndY4;
        // Above contexts (frame-wide, 4x4 / chroma-4x4 / 8x8 units) and left contexts (one 64x64 superblock row).
        public byte[] AMode = null!, AUvMode = null!, ASkip = null!, ALCoef = null!, ACU = null!, ACV = null!, APart = null!;
        public byte[] LMode = null!, LUvMode = null!, LSkip = null!, LLCoef = null!, LCU = null!, LCV = null!, LPart = null!;
    }

    /// <summary>Codes the planes as a lossless tile group (one entropy-coded tile per <see cref="Av1ObuWriter.TileLayout"/>
    /// tile). <paramref name="u"/>/<paramref name="v"/> are null for a monochrome stream. Planes are the padded
    /// (MI-aligned) planes; the reconstruction is these samples exactly.</summary>
    internal static byte[] EncodeTiles(ushort[] y, ushort[]? u, ushort[]? v, int w, int bw4, int bh4, int sbCols, int sbRows,
        Av1PixelLayout layout, int bitDepth, bool edgeFilter)
    {
        bool chroma = u != null && layout != Av1PixelLayout.I400;
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 ? 1 : 0;
        var c = new Ctx
        {
            Y = y, U = u ?? [], V = v ?? [], W = w, Cw = chroma ? w >> ssX : 0, Bw4 = bw4, Bh4 = bh4, SsX = ssX, SsY = ssY,
            Bd = bitDepth, Chroma = chroma, EdgeFilter = edgeFilter, Layout = chroma ? layout : Av1PixelLayout.I400,
        };
        var (_, _, colStart, rowStart) = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        for (int tr = 0; tr + 1 < rowStart.Length; tr++)
            for (int tc = 0; tc + 1 < colStart.Length; tc++)
            {
                c.Msac = new Av1MsacWriter();
                c.Cdf = new Av1CdfContext();
                Av1CdfDefaults.InitializeDefault(c.Cdf, 0);   // qcat 0 (base_q_idx <= 20)
                c.TileX4 = colStart[tc] * 16; c.TileY4 = rowStart[tr] * 16;
                c.TileEndX4 = Math.Min(colStart[tc + 1] * 16, bw4); c.TileEndY4 = Math.Min(rowStart[tr + 1] * 16, bh4);
                int cw4 = (bw4 + ssX) >> ssX;
                c.AMode = new byte[bw4]; c.ASkip = new byte[bw4]; c.ALCoef = Filled(bw4, 0x40); c.APart = new byte[(bw4 + 1) >> 1];
                c.AUvMode = new byte[cw4]; c.ACU = Filled(cw4, 0x40); c.ACV = Filled(cw4, 0x40);
                for (int sby = rowStart[tr]; sby < rowStart[tr + 1]; sby++)
                {
                    c.LMode = new byte[16]; c.LSkip = new byte[16]; c.LLCoef = Filled(16, 0x40); c.LPart = new byte[8];
                    c.LUvMode = new byte[16]; c.LCU = Filled(16, 0x40); c.LCV = Filled(16, 0x40);
                    for (int sbx = colStart[tc]; sbx < colStart[tc + 1]; sbx++)
                        Partition(c, 1, sbx * 16, sby * 16, 0);
                }
                tiles.Add(c.Msac.Finish());
            }
        return Av1StillImageEncoder.AssembleTileGroup(tiles);
    }

    private static byte[] Filled(int n, byte v) { var a = new byte[n]; Array.Fill(a, v); return a; }

    // Recursive split to 8x8 (bl: 1 = 64x64 .. 4 = 8x8), with the frame-edge implicit / bool partitions of the spec.
    private static void Partition(Ctx c, int bl, int bx4, int by4, int edgeIdx)
    {
        ref readonly var node = ref Av1IntraEdgeTree.Tree64[edgeIdx];
        int hsz = 16 >> bl;
        int bx8 = bx4 >> 1, by8 = (by4 & 15) >> 1;
        int partCtx = ((c.APart[bx8] >> (4 - bl)) & 1) + (((c.LPart[by8] >> (4 - bl)) & 1) << 1);
        Span<ushort> partCdf = c.Cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
        int nPart = Av1Tables.PartitionTypeCount[bl];
        if (bl == 4)
        {
            c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.None, nPart);
            Block(c, bx4, by4, node.O);
            c.APart[bx8] = Av1Tables.AboveLeftPartCtx[0, bl, (int)Av1BlockPartition.None];
            c.LPart[by8] = Av1Tables.AboveLeftPartCtx[1, bl, (int)Av1BlockPartition.None];
            return;
        }
        bool haveH = c.Bw4 > bx4 + hsz, haveV = c.Bh4 > by4 + hsz;
        int C(int q) => Av1IntraEdgeTree.GetSplitChild(Av1IntraEdgeTree.Tree64[edgeIdx], q);
        if (!haveH && !haveV)
        {
            Partition(c, bl + 1, bx4, by4, C(0));
            return;
        }
        if (haveH && !haveV)
        {
            c.Msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
            Partition(c, bl + 1, bx4, by4, C(0));
            Partition(c, bl + 1, bx4 + hsz, by4, C(1));
            return;
        }
        if (!haveH && haveV)
        {
            c.Msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
            Partition(c, bl + 1, bx4, by4, C(0));
            Partition(c, bl + 1, bx4, by4 + hsz, C(2));
            return;
        }
        c.Msac.EncodeSymbolAdapt(partCdf, (int)Av1BlockPartition.Split, nPart);
        Partition(c, bl + 1, bx4, by4, C(0));
        Partition(c, bl + 1, bx4 + hsz, by4, C(1));
        Partition(c, bl + 1, bx4, by4 + hsz, C(2));
        Partition(c, bl + 1, bx4 + hsz, by4 + hsz, C(3));
    }

    private static bool IsDirectional(Av1IntraPredMode m) => m >= Av1IntraPredMode.Vertical && m <= Av1IntraPredMode.VerticalLeft;
    private static bool IsSmooth(int m) => m is (int)Av1IntraPredMode.Smooth or (int)Av1IntraPredMode.SmoothV or (int)Av1IntraPredMode.SmoothH;

    // One transform block's layout inside an 8x8 block: (offset in the plane's 4-units, per-tx edge flags). dav1d
    // recon_b_intra: top-right exists unless (not the first tx row or the block has none) and the tx is at the
    // block's right; bottom-left exists only in the first tx column and (the block has one or it is not the last row).
    private static List<(int X, int Y, Av1EdgeFlags E)> TxGrid(int cw4, int ch4, bool hasTr, bool hasBl)
    {
        var l = new List<(int, int, Av1EdgeFlags)>();
        for (int y = 0; y < ch4; y++)
            for (int x = 0; x < cw4; x++)
                l.Add((x, y, (((y > 0 || !hasTr) && x + 1 >= cw4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                             ((x > 0 || (!hasBl && y + 1 >= ch4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom)));
        return l;
    }

    // Prediction for one 4x4 transform of a plane, straight from the source (== reconstruction) with the decoder's
    // availability: edges limited to the tile (dav1d prepare_intra_edges x > col_start, w = col_end).
    private static void Predict4(Ctx c, ushort[] plane, int stride, bool isChroma, int x4, int y4, Av1IntraPredMode mode,
        Av1EdgeFlags edge, int intraFlags, Span<ushort> dst)
    {
        int sx = isChroma ? c.SsX : 0, sy = isChroma ? c.SsY : 0;
        int x0 = c.TileX4 >> sx, y0 = c.TileY4 >> sy, x1 = c.TileEndX4 >> sx, y1 = c.TileEndY4 >> sy;
        int pbw4 = isChroma ? (c.Bw4 + sx) >> sx : c.Bw4, pbh4 = isChroma ? (c.Bh4 + sy) >> sy : c.Bh4;
        Span<ushort> e = stackalloc ushort[257];
        int angle = 0;
        int m = Av1Reconstruction.PrepareIntraEdges(x4, x4 > x0, y4, y4 > y0, x1, y1, edge,
            plane, (y4 * 4) * stride + x4 * 4, stride, default, mode, ref angle, 1, 1,
            filterEdge: (intraFlags & EdgeFilterBit) != 0, e, 128, c.Bd);
        var tmp = new ushort[16];
        Av1IntraPred.Predict16(m, tmp, 4, e, 128, 4, 4, angle | intraFlags, 4 * pbw4 - 4 * x4, 4 * pbh4 - 4 * y4, c.Bd);
        tmp.CopyTo(dst);
    }

    // libaom av1_fwht4x4 (the exact inverse of the decoder's WHT): residual (row-major) -> levels in the decoder's
    // coefficient order (column-major, rc = x * 4 + y). The UNIT_QUANT_FACTOR scaling cancels against the lossless
    // quantizer (dq = 4) and the inverse's >> 2, so the level is the raw lifting output.
    internal static void Fwht4(ReadOnlySpan<int> res, Span<int> levels)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = res[0 * 4 + i], b1 = res[1 * 4 + i], c1 = res[2 * 4 + i], d1 = res[3 * 4 + i];
            a1 += b1; d1 -= c1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1; c1 = e1 - c1;
            a1 -= c1; d1 += b1;
            t[0 * 4 + i] = a1; t[1 * 4 + i] = c1; t[2 * 4 + i] = d1; t[3 * 4 + i] = b1;
        }
        for (int r = 0; r < 4; r++)
        {
            int a1 = t[r * 4 + 0], b1 = t[r * 4 + 1], c1 = t[r * 4 + 2], d1 = t[r * 4 + 3];
            a1 += b1; d1 -= c1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1; c1 = e1 - c1;
            a1 -= c1; d1 += b1;
            // output row r = (a1, c1, d1, b1) at columns 0..3 → decoder rc = col * 4 + r
            levels[0 * 4 + r] = a1; levels[1 * 4 + r] = c1; levels[2 * 4 + r] = d1; levels[3 * 4 + r] = b1;
        }
    }

    // Levels of every transform of a plane for a mode, and their total magnitude (the mode-decision cost).
    private static long PlaneLevels(Ctx c, ushort[] plane, int stride, bool isChroma, int bx, int by, Av1IntraPredMode mode,
        List<(int X, int Y, Av1EdgeFlags E)> grid, int intraFlags, int[][] outLevels)
    {
        Span<ushort> pred = stackalloc ushort[16];
        Span<int> res = stackalloc int[16];
        long cost = 0;
        for (int k = 0; k < grid.Count; k++)
        {
            var (x, y, e) = grid[k];
            int x4 = bx + x, y4 = by + y;
            Predict4(c, plane, stride, isChroma, x4, y4, mode, e, intraFlags, pred);
            for (int r = 0; r < 4; r++)
                for (int q = 0; q < 4; q++)
                    res[r * 4 + q] = plane[(y4 * 4 + r) * stride + x4 * 4 + q] - pred[r * 4 + q];
            var lv = outLevels[k] ??= new int[16];
            Fwht4(res, lv);
            foreach (int l in lv) cost += Math.Abs(l);
        }
        return cost;
    }

    private static byte CfCtx(int[] levels)
    {
        int cul = 0;
        foreach (int l in levels) cul += Math.Abs(l);
        int dcSign = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
        return (byte)(Math.Min(cul, 63) | dcSign);
    }

    private static void Block(Ctx c, int bx4, int by4, Av1EdgeFlags blockEdge)
    {
        int ly = by4 & 15, cbx4 = bx4 >> c.SsX, cby4 = by4 >> c.SsY, lcy = ly >> c.SsY;
        // Luma: 2x2 transforms; the block's own top-right / bottom-left availability comes from the edge tree.
        var lumaGrid = TxGrid(2, 2, (blockEdge & Av1EdgeFlags.I444TopHasRight) != 0, (blockEdge & Av1EdgeFlags.I444LeftHasBottom) != 0);
        int yFlags = c.EdgeFilter ? EdgeFilterBit | (IsSmooth(c.AMode[bx4]) || IsSmooth(c.LMode[ly]) ? SmoothBit : 0) : 0;
        var bestY = new int[4][]; long bestYCost = long.MaxValue; var yMode = Av1IntraPredMode.Dc;
        var cand = new int[4][];
        foreach (var m in Modes)
        {
            long cost = PlaneLevels(c, c.Y, c.W, false, bx4, by4, m, lumaGrid, yFlags, cand);
            if (cost < bestYCost) { bestYCost = cost; yMode = m; for (int k = 0; k < 4; k++) bestY[k] = (int[])cand[k].Clone(); }
        }

        // Chroma: (8 >> ss) square-ish block of 4x4 transforms with the layout's own edge bits.
        int ccw4 = 2 >> c.SsX, cch4 = 2 >> c.SsY;
        List<(int X, int Y, Av1EdgeFlags E)> cGrid = [];
        int[][] bestU = [], bestV = [];
        var uvMode = Av1IntraPredMode.Dc;
        if (c.Chroma)
        {
            int lsh = (int)c.Layout - 1;
            bool cTr = (blockEdge & (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> lsh)) != 0;
            bool cBl = (blockEdge & (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> lsh)) != 0;
            cGrid = TxGrid(ccw4, cch4, cTr, cBl);
            int uvFlags = c.EdgeFilter ? EdgeFilterBit | (IsSmooth(c.AUvMode[cbx4]) || IsSmooth(c.LUvMode[lcy]) ? SmoothBit : 0) : 0;
            long best = long.MaxValue;
            var cu = new int[cGrid.Count][]; var cv = new int[cGrid.Count][];
            foreach (var m in Modes)
            {
                long cost = PlaneLevels(c, c.U, c.Cw, true, cbx4, cby4, m, cGrid, uvFlags, cu)
                          + PlaneLevels(c, c.V, c.Cw, true, cbx4, cby4, m, cGrid, uvFlags, cv);
                if (cost < best)
                {
                    best = cost; uvMode = m;
                    bestU = Array.ConvertAll(cu, a => (int[])a.Clone()); bestV = Array.ConvertAll(cv, a => (int[])a.Clone());
                }
            }
        }

        bool anyNonZero = bestYCost > 0 || Array.Exists(bestU, a => Array.Exists(a, l => l != 0)) || Array.Exists(bestV, a => Array.Exists(a, l => l != 0));
        int skip = anyNonZero ? 0 : 1;

        // mode info (key frame): skip, y mode (+angle), uv mode (+angle); no palette / filter-intra / tx symbols.
        c.Msac.EncodeBoolAdapt(c.Cdf.GetSkipCdf(c.ASkip[bx4] + c.LSkip[ly]), (uint)skip);
        c.Msac.EncodeSymbolAdapt(c.Cdf.GetKfYModeCdf(Av1Tables.IntraModeContext[c.AMode[bx4]], Av1Tables.IntraModeContext[c.LMode[ly]]), (int)yMode, 12);
        if (IsDirectional(yMode))
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)yMode - (int)Av1IntraPredMode.Vertical), 3, 6);
        if (c.Chroma)
        {
            bool cflAllowed = ccw4 == 1 && cch4 == 1;   // lossless: CfL only for a 4x4 chroma block
            c.Msac.EncodeSymbolAdapt(c.Cdf.GetUvModeCdf(cflAllowed, (int)yMode), (int)uvMode, cflAllowed ? 13 : 12);
            if (IsDirectional(uvMode))
                c.Msac.EncodeSymbolAdapt(c.Cdf.GetAngleDeltaCdf((int)uvMode - (int)Av1IntraPredMode.Vertical), 3, 6);
        }

        if (skip == 0)
        {
            ref readonly var t4 = ref Av1Tables.TxfmDimensions[Tx4];
            for (int k = 0; k < 4; k++)
            {
                var (x, y, _) = lumaGrid[k];
                int sk = Av1CoeffDecode.GetSkipCtx(in t4, Bs8, c.ALCoef.AsSpan(bx4 + x), c.LLCoef.AsSpan(ly + y), 0, (int)c.Layout);
                int sg = Av1CoeffDecode.GetDcSignCtx(Tx4, c.ALCoef.AsSpan(bx4 + x), c.LLCoef.AsSpan(ly + y));
                Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, Tx4, 0, (int)yMode, bestY[k], skipCtx: sk, dcSignCtx: sg, lossless: true);
                c.ALCoef[bx4 + x] = c.LLCoef[ly + y] = CfCtx(bestY[k]);
            }
            if (c.Chroma)
                for (int pl = 0; pl < 2; pl++)
                {
                    byte[] a = pl == 0 ? c.ACU : c.ACV, l = pl == 0 ? c.LCU : c.LCV;
                    var lv = pl == 0 ? bestU : bestV;
                    for (int k = 0; k < cGrid.Count; k++)
                    {
                        var (x, y, _) = cGrid[k];
                        int sk = Av1CoeffDecode.GetSkipCtx(in t4, Bs8, a.AsSpan(cbx4 + x), l.AsSpan(lcy + y), 1, (int)c.Layout);
                        int sg = Av1CoeffDecode.GetDcSignCtx(Tx4, a.AsSpan(cbx4 + x), l.AsSpan(lcy + y));
                        Av1CoeffEncode.EncodeCoefs(c.Msac, c.Cdf.Coef, c.Cdf.Mode, Tx4, 1, 0, lv[k], skipCtx: sk, dcSignCtx: sg, lossless: true);
                        a[cbx4 + x] = l[lcy + y] = CfCtx(lv[k]);
                    }
                }
        }
        else
        {
            for (int i = 0; i < 2; i++) { c.ALCoef[bx4 + i] = 0x40; c.LLCoef[ly + i] = 0x40; }
            if (c.Chroma)
            {
                for (int i = 0; i < ccw4; i++) { c.ACU[cbx4 + i] = 0x40; c.ACV[cbx4 + i] = 0x40; }
                for (int i = 0; i < cch4; i++) { c.LCU[lcy + i] = 0x40; c.LCV[lcy + i] = 0x40; }
            }
        }

        for (int i = 0; i < 2; i++) { c.AMode[bx4 + i] = (byte)yMode; c.ASkip[bx4 + i] = (byte)skip; c.LMode[ly + i] = (byte)yMode; c.LSkip[ly + i] = (byte)skip; }
        if (c.Chroma)
        {
            for (int i = 0; i < ccw4; i++) c.AUvMode[cbx4 + i] = (byte)uvMode;
            for (int i = 0; i < cch4; i++) c.LUvMode[lcy + i] = (byte)uvMode;
        }
    }
}
