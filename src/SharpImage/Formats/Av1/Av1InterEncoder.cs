// AV1 inter-frame (P-frame) encoder for image sequences. Every block of an inter frame is predicted from reference
// slot 0 (LAST: the previous decoded frame) with a single motion vector — NEARESTMV / NEARMV / GLOBALMV / NEWMV —
// and codes its residual with the largest transform for the block (TX_MODE_LARGEST, reduced inter transform set:
// DCT_DCT). Partitions are square (NONE / SPLIT, 64x64 down to 8x8, forced splits at the frame edges). Frames
// start from the default CDFs (primary_ref_frame NONE), so nothing but the reference picture carries over.
//
// The coder mirrors the decoder's parse (Av1Decode.DecodeSuperblock / DecodeBlock / DecodeBlockInter and
// Av1Reconstruction.ReconBlockInter) symbol for symbol and keeps the same context state — the decoder's
// Av1BlockContextManaged above/left arrays and the refmvs candidate grid (Av1RefMvs) — so contexts, MV candidate
// stacks and CDF adaptation agree by construction. Each superblock is analysed first (rate-distortion choice of
// partition and motion vector against the real candidate stacks) and then written; the written syntax re-derives
// the cheapest way to signal each chosen motion vector from the stack the decoder will see.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpImage.Formats.Av1;

internal static class Av1InterEncoder
{
    /// <summary>A picture at the coded bit depth: luma plus (unless monochrome) chroma planes, tightly packed.</summary>
    internal sealed class Picture
    {
        public required ushort[] Y;
        public ushort[]? U, V;
        public required int Width, Height;
    }

    // Motion search: full-pel radius around the best predictor (in luma pixels), then half- and quarter-pel.
    internal static int SearchRange = 24;

    /// <summary>
    /// Encodes <paramref name="src"/> as an INTER_FRAME predicted from <paramref name="reference"/> (the decoder's
    /// reconstruction of the previous frame, stored in slot 0). The caller holds the <see cref="Av1ObuWriter.LayeredStream"/>
    /// scope with <c>InterFrame</c> set. Returns the OBU_FRAME.
    /// </summary>
    internal static byte[] EncodeFrameObu(Picture src, Picture reference, int bitDepth, Av1PixelLayout layout, int baseQIdx,
        Av1ObuWriter.CdefParams cdef, int lfLevel)
    {
        int w = src.Width, h = src.Height;
        int sbCols = (w + 63) >> 6, sbRows = (h + 63) >> 6;
        bool mono = layout == Av1PixelLayout.I400;
        byte[] hdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, mono, txModeSelect: false,
            cdef, lfLevel, screenContentTools: false, reducedTxSet: true);
        var enc = new FrameCoder(src, reference, bitDepth, layout, baseQIdx);
        var tl = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        for (int tr = 0; tr < (1 << tl.RowsLog2) && tl.RowStartSb[tr] < sbRows; tr++)
            for (int tc = 0; tc < (1 << tl.ColsLog2) && tl.ColStartSb[tc] < sbCols; tc++)
                tiles.Add(enc.EncodeTile(tl.ColStartSb[tc] << 4, Math.Min(tl.ColStartSb[tc + 1] << 4, enc.Bw),
                    tl.RowStartSb[tr] << 4, Math.Min(tl.RowStartSb[tr + 1] << 4, enc.Bh), tc == 0 ? tr : -1));
        byte[] tg = Av1StillImageEncoder.AssembleTileGroup(tiles);
        var payload = new byte[hdr.Length + tg.Length];
        hdr.CopyTo(payload, 0);
        tg.CopyTo(payload, hdr.Length);
        return Av1ObuWriter.WrapObu(Av1ObuType.Frame, payload);
    }

    // One analysed leaf block: its size, motion vector and quantized levels per transform block (luma, then U, V,
    // in the decoder's order), or skip when every level is zero.
    private sealed class Leaf
    {
        public int Bx, By, Bs, EdgeIdx;
        public Av1EdgeFlags Edge;
        public Av1MotionVector Mv;
        public bool Skip;
        public List<int[]> LumaLevels = [], ULevels = [], VLevels = [];
    }

    // Analysis tree: a leaf, a coded split (NONE vs SPLIT chosen), or an implicit / edge split.
    private sealed class Node
    {
        public int Bl, Bx, By, EdgeIdx;
        public Leaf? Leaf;
        public Node?[]? Children;   // 4 entries (quadrant order); null entries lie outside the frame
    }

    private sealed class FrameCoder
    {
        public readonly Picture Src, Ref;
        public readonly int W, H, Bd, SsX, SsY, CW, CH, Bw, Bh, PadW, PadH, BdIdx;
        public readonly Av1PixelLayout Layout;
        public readonly bool Mono;
        public readonly int BaseQIdx, DcDq, AcDq, Qcat;
        public readonly double Lambda, LambdaSad;
        public readonly ushort[] SrcY, SrcU, SrcV;          // padded to PadW x PadH (edge-replicated)
        public readonly Av1RefMvsFrame Rf = new();
        public readonly Av1DecoderFrameHeader Fh;
        public readonly ushort[] Emu = new ushort[(128 + 16) * 192];

        public FrameCoder(Picture src, Picture reference, int bitDepth, Av1PixelLayout layout, int baseQIdx)
        {
            Src = src; Ref = reference; Bd = bitDepth; Layout = layout; BaseQIdx = baseQIdx;
            W = src.Width; H = src.Height;
            Mono = layout == Av1PixelLayout.I400;
            SsX = layout is Av1PixelLayout.I420 or Av1PixelLayout.I422 ? 1 : 0;
            SsY = layout == Av1PixelLayout.I420 ? 1 : 0;
            CW = (W + SsX) >> SsX; CH = (H + SsY) >> SsY;
            Bw = ((W + 7) >> 3) << 1; Bh = ((H + 7) >> 3) << 1;
            PadW = ((W + 63) >> 6) << 6; PadH = ((H + 63) >> 6) << 6;
            BdIdx = bitDepth == 8 ? 0 : bitDepth == 10 ? 1 : 2;
            DcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 0];
            AcDq = Av1Tables.DequantTable[BdIdx, baseQIdx, 1];
            Qcat = (baseQIdx > 20 ? 1 : 0) + (baseQIdx > 60 ? 1 : 0) + (baseQIdx > 120 ? 1 : 0);
            Lambda = Av1StillImageEncoder.RdLambdaK * AcDq * AcDq;
            LambdaSad = Math.Sqrt(Lambda);
            SrcY = Pad(src.Y, W, H, PadW, PadH);
            SrcU = Mono ? [] : Pad(src.U!, CW, CH, PadW >> SsX, PadH >> SsY);
            SrcV = Mono ? [] : Pad(src.V!, CW, CH, PadW >> SsX, PadH >> SsY);
            Fh = new Av1DecoderFrameHeader { CodedWidth = W, SuperResUpscaledWidth = W, Height = H, Hp = false, ForceIntegerMv = false };
            var seq = new Av1DecoderSequenceHeader();
            Av1RefMvs.InitFrame(Rf, seq, Fh, stackalloc byte[7], null, null, null);
        }

        private static ushort[] Pad(ushort[] p, int w, int h, int pw, int ph)
        {
            var o = new ushort[pw * ph];
            for (int y = 0; y < ph; y++)
            {
                int sy = Math.Min(y, h - 1);
                p.AsSpan(sy * w, w).CopyTo(o.AsSpan(y * pw, w));
                if (pw > w) o.AsSpan(y * pw + w, pw - w).Fill(p[sy * w + w - 1]);
            }
            return o;
        }

        // ── Tile ─────────────────────────────────────────────────────────────────────────────────────────────────
        private Av1MsacWriter msac = null!;
        private Av1CdfContext cdf = null!;
        private Av1BlockContextManaged[] aboveRow = null!;
        private Av1BlockContextManaged left = null!;
        private readonly Av1RefMvsTile rt = new();
        private int colStart, colEnd, rowStart, rowEnd;
        private Av1BlockContextManaged above = null!;
        private readonly Dictionary<int, Av1BlockContextManaged[]> aboveByTileRow = new();

        public byte[] EncodeTile(int cs, int ce, int rs, int re, int newTileRow)
        {
            colStart = cs; colEnd = ce; rowStart = rs; rowEnd = re;
            cdf = new Av1CdfContext();
            Av1CdfDefaults.InitializeDefault(cdf, Qcat);
            msac = new Av1MsacWriter();
            // Above contexts: one per SB128 column for each tile row, reset for the frame (Av1Decoder: aboveCtx per
            // (sb128 column, tile row), Reset(isIntra = false)).
            int key = rs;
            if (!aboveByTileRow.TryGetValue(key, out var row))
            {
                row = new Av1BlockContextManaged[(Bw + 31) >> 5];
                for (int i = 0; i < row.Length; i++) { row[i] = new Av1BlockContextManaged(); row[i].Reset(false); }
                aboveByTileRow[key] = row;
            }
            aboveRow = row;
            left = new Av1BlockContextManaged();
            for (int by = rs; by < re; by += 16)
            {
                left.Reset(false);
                Av1RefMvs.TileSbRowInit(rt, Rf, cs, ce, rs, re, by >> 4);
                for (int bx = cs; bx < ce; bx += 16)
                {
                    above = aboveRow[bx >> 5];
                    var root = Analyze((int)Av1BlockLevel.Bl64x64, bx, by, 0);
                    Write(root);
                }
            }
            return msac.Finish();
        }

        // ── Analysis (partition + motion) ──────────────────────────────────────────────────────────────────────
        private Node Analyze(int bl, int bx, int by, int edgeIdx) => Analyze(bl, bx, by, edgeIdx, out _);

        // Rate-distortion partition choice. j = D + lambda * R of the subtree (partition symbols included).
        private Node Analyze(int bl, int bx, int by, int edgeIdx, out double j)
        {
            var node = new Node { Bl = bl, Bx = bx, By = by, EdgeIdx = edgeIdx };
            int hsz = 16 >> bl;
            bool haveH = Bw > bx + hsz, haveV = Bh > by + hsz;
            ref readonly var en = ref Av1IntraEdgeTree.Tree64[edgeIdx];
            if (!haveH && !haveV)
            {
                node.Children = [Analyze(bl + 1, bx, by, Av1IntraEdgeTree.GetSplitChild(en, 0), out j), null, null, null];
                return node;
            }
            if (!haveH || !haveV)
            {
                // Frame edge: always split (the other choice is a rectangular HORZ / VERT block).
                node.Children = new Node?[4];
                node.Children[0] = Analyze(bl + 1, bx, by, Av1IntraEdgeTree.GetSplitChild(en, 0), out double j0);
                double j1;
                if (haveH) node.Children[1] = Analyze(bl + 1, bx + hsz, by, Av1IntraEdgeTree.GetSplitChild(en, 1), out j1);
                else node.Children[2] = Analyze(bl + 1, bx, by + hsz, Av1IntraEdgeTree.GetSplitChild(en, 2), out j1);
                j = j0 + j1 + Lambda;
                return node;
            }

            int bs = SquareBs(bl);
            var leaf = EvaluateLeaf(bs, bx, by, en.O, edgeIdx, out double jNone);
            jNone += Lambda * PartitionBits(bl, (int)Av1BlockPartition.None);
            if (bl == (int)Av1BlockLevel.Bl8x8)
            {
                node.Leaf = leaf;
                Splat(leaf);
                j = jNone;
                return node;
            }
            // SPLIT: the children see each other's motion (splatted as they are chosen); restore if NONE wins.
            var saved = SaveGrid(bx, by, hsz * 2);
            var kids = new Node?[4];
            double jSplit = Lambda * PartitionBits(bl, (int)Av1BlockPartition.Split);
            for (int q = 0; q < 4 && jSplit < jNone; q++)
            {
                int cx = bx + ((q & 1) != 0 ? hsz : 0), cy = by + ((q & 2) != 0 ? hsz : 0);
                if (cx >= Bw || cy >= Bh) continue;
                kids[q] = Analyze(bl + 1, cx, cy, Av1IntraEdgeTree.GetSplitChild(en, q), out double jc);
                jSplit += jc;
            }
            if (jSplit < jNone)
            {
                node.Children = kids;
                j = jSplit;
                return node;
            }
            RestoreGrid(saved, bx, by, hsz * 2);
            node.Leaf = leaf;
            Splat(leaf);
            j = jNone;
            return node;
        }

        private static int SquareBs(int bl) => bl switch
        {
            (int)Av1BlockLevel.Bl64x64 => (int)Av1BlockSize.Bs64x64,
            (int)Av1BlockLevel.Bl32x32 => (int)Av1BlockSize.Bs32x32,
            (int)Av1BlockLevel.Bl16x16 => (int)Av1BlockSize.Bs16x16,
            _ => (int)Av1BlockSize.Bs8x8,
        };

        private double PartitionBits(int bl, int part) => Av1CoeffEncode.SymBits(cdf.GetPartitionCdf((Av1BlockLevel)bl, 0), part);

        // The best motion vector (and its residual) for a square block, with its rate-distortion cost.
        private Leaf EvaluateLeaf(int bs, int bx, int by, Av1EdgeFlags edge, int edgeIdx, out double bestJ)
        {
            int n = 4 * Av1Tables.BlockDimensions[bs, 0];
            Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
            Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int modeCtx, out _,
                new Av1RefMvsRefPair { Ref0 = 1, Ref1 = -1 }, bs, edge, by, bx);

            // Candidate predictors: zero (GLOBALMV), the stack (NEAREST / NEAR), then a motion search seeded by them.
            var cands = new List<Av1MotionVector> { default };
            int nStack = Math.Max(nCand, 2);
            for (int i = 0; i < Math.Min(nStack, 4); i++)
            {
                var m = stack[i].Mv.Mv0;
                FixQuarter(ref m);
                if (!cands.Contains(m)) cands.Add(m);
            }
            var searched = MotionSearch(bx, by, n, cands);
            if (!cands.Contains(searched)) cands.Add(searched);

            bestJ = double.MaxValue;
            Leaf? best = null;
            foreach (var mv in cands)
            {
                if (!MvInRange(mv)) continue;
                var leaf = new Leaf { Bx = bx, By = by, Bs = bs, EdgeIdx = edgeIdx, Edge = edge, Mv = mv };
                double modeBits = ModeBits(stack, nCand, modeCtx, mv);
                double j = CodeResidual(leaf, n, modeBits);
                if (j < bestJ) { bestJ = j; best = leaf; }
            }
            return best!;
        }

        private static bool MvInRange(Av1MotionVector mv) => Math.Abs((int)mv.X) < (1 << 14) && Math.Abs((int)mv.Y) < (1 << 14);

        private static void FixQuarter(ref Av1MotionVector m)
        {
            m.X = (short)((m.X - (m.X >> 15)) & ~1);
            m.Y = (short)((m.Y - (m.Y >> 15)) & ~1);
        }

        // Approximate bits to signal mv as the cheapest of NEAREST / NEAR / GLOBAL / NEW against the stack.
        private double ModeBits(ReadOnlySpan<Av1RefMvsCandidate> stack, int nCand, int modeCtx, Av1MotionVector mv)
        {
            var m = cdf.Mode;
            double notNew = Av1CoeffEncode.SymBits(m.NewmvMode[modeCtx & 7], 1);
            var nearest = stack[0].Mv.Mv0; FixQuarter(ref nearest);
            if (mv.Equals(nearest))
                return notNew + Av1CoeffEncode.SymBits(m.GlobalmvMode[(modeCtx >> 3) & 1], 1) + Av1CoeffEncode.SymBits(m.RefmvMode[(modeCtx >> 4) & 15], 0);
            int maxDrl = nCand > 2 ? Math.Min(nCand - 1, 3) : 1;
            for (int d = 1; d <= maxDrl; d++)
            {
                var near = stack[d].Mv.Mv0;
                if (d < 2) FixQuarter(ref near);
                if (mv.Equals(near))
                    return notNew + Av1CoeffEncode.SymBits(m.GlobalmvMode[(modeCtx >> 3) & 1], 1)
                        + Av1CoeffEncode.SymBits(m.RefmvMode[(modeCtx >> 4) & 15], 1) + d;
            }
            if (mv.X == 0 && mv.Y == 0)
                return notNew + Av1CoeffEncode.SymBits(m.GlobalmvMode[(modeCtx >> 3) & 1], 0);
            double bestMv = double.MaxValue;
            int drls = nCand > 1 ? Math.Min(nCand, 3) : 1;
            for (int d = 0; d < drls; d++)
            {
                var r = nCand > 1 ? stack[d].Mv.Mv0 : stack[0].Mv.Mv0;
                if (nCand <= 1) FixQuarter(ref r);
                if (((mv.X - r.X) & 1) != 0 || ((mv.Y - r.Y) & 1) != 0) continue;
                bestMv = Math.Min(bestMv, MvBits(mv, r) + d);
            }
            return Av1CoeffEncode.SymBits(m.NewmvMode[modeCtx & 7], 0) + bestMv;
        }

        private double MvBits(Av1MotionVector mv, Av1MotionVector refMv)
        {
            int dy = mv.Y - refMv.Y, dx = mv.X - refMv.X;
            int joint = (dy != 0 ? 2 : 0) | (dx != 0 ? 1 : 0);
            double bits = Av1CoeffEncode.SymBits(cdf.Mv.Joint, joint);
            if (dy != 0) bits += MvCompBits(cdf.Mv.Comp0, dy);
            if (dx != 0) bits += MvCompBits(cdf.Mv.Comp1, dx);
            return bits;
        }

        // Sign + class + integer bits + 2 fraction bits (probabilities of the bits beyond the class approximated).
        private static double MvCompBits(Av1CdfMvComponent c, int diff)
        {
            MvCompSplit(diff, out _, out int cl, out _, out _);
            return 1 + Av1CoeffEncode.SymBits(c.Classes, cl) + (cl == 0 ? 1 : cl) + 2;
        }

        // diff (nonzero, even: quarter-pel) -> sign, class, integer part (up), fraction (fp); hp is implied 1.
        private static void MvCompSplit(int diff, out bool sign, out int cl, out int up, out int fp)
        {
            sign = diff < 0;
            int mag = Math.Abs(diff) - 1;   // (up << 3) | (fp << 1) | hp
            up = mag >> 3;
            fp = (mag >> 1) & 3;
            cl = up < 2 ? 0 : 31 - BitOperations.LeadingZeroCount((uint)up);
        }

        // Luma: full-pel search (SAD + MV rate) seeded by the predictors, then half- and quarter-pel refinement on the
        // interpolated prediction (SATD).
        private Av1MotionVector MotionSearch(int bx, int by, int n, List<Av1MotionVector> seeds)
        {
            int px = bx * 4, py = by * 4;
            long bestCost = long.MaxValue;
            int bmx = 0, bmy = 0;
            foreach (var s in seeds)
            {
                int sx = (s.X + 4) >> 3, sy = (s.Y + 4) >> 3;
                long c = SadFull(px, py, n, sx, sy) + (long)(LambdaSad * (Math.Abs(sx) + Math.Abs(sy)) * 0.5);
                if (c < bestCost) { bestCost = c; bmx = sx; bmy = sy; }
            }
            // Diamond steps down to 1 px, within SearchRange of the best seed.
            int cx0 = bmx, cy0 = bmy;
            for (int step = Math.Max(1, SearchRange / 2); step >= 1; step >>= 1)
            {
                bool moved = true;
                while (moved)
                {
                    moved = false;
                    int ox = bmx, oy = bmy;
                    foreach (var (dx, dy) in Diamond)
                    {
                        int mx = ox + dx * step, my = oy + dy * step;
                        if (Math.Abs(mx - cx0) > SearchRange || Math.Abs(my - cy0) > SearchRange) continue;
                        long c = SadFull(px, py, n, mx, my) + (long)(LambdaSad * (Math.Abs(mx) + Math.Abs(my)) * 0.5);
                        if (c < bestCost) { bestCost = c; bmx = mx; bmy = my; moved = true; }
                    }
                }
            }
            // Sub-pel: 1/2 then 1/4 pel (MV units of 1/8), on the exact 8-tap prediction.
            var best = new Av1MotionVector { X = (short)(bmx * 8), Y = (short)(bmy * 8) };
            long bestSatd = SatdMc(px, py, n, best);
            foreach (int step in new[] { 4, 2 })
            {
                var center = best;
                foreach (var (dx, dy) in Ring)
                {
                    var m = new Av1MotionVector { X = (short)(center.X + dx * step), Y = (short)(center.Y + dy * step) };
                    long c = SatdMc(px, py, n, m);
                    if (c < bestSatd) { bestSatd = c; best = m; }
                }
            }
            return best;
        }

        private static readonly (int, int)[] Diamond = [(0, -1), (-1, 0), (1, 0), (0, 1)];
        private static readonly (int, int)[] Ring = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

        private long SadFull(int px, int py, int n, int mx, int my)
        {
            long sad = 0;
            var r = Ref.Y;
            int rw = Ref.Width, rh = Ref.Height;
            for (int y = 0; y < n; y++)
            {
                int ry = Math.Clamp(py + y + my, 0, rh - 1);
                int so = (py + y) * PadW + px;
                for (int x = 0; x < n; x++)
                {
                    int rx = Math.Clamp(px + x + mx, 0, rw - 1);
                    sad += Math.Abs(SrcY[so + x] - r[ry * rw + rx]);
                }
            }
            return sad;
        }

        private readonly ushort[] mcBuf = new ushort[64 * 64];

        private long SatdMc(int px, int py, int n, Av1MotionVector mv)
        {
            McPlane(0, px >> 2, py >> 2, n, n, mv, mcBuf, n);
            long satd = 0;
            Span<int> d = stackalloc int[64];
            for (int y0 = 0; y0 < n; y0 += 8)
                for (int x0 = 0; x0 < n; x0 += 8)
                {
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                            d[y * 8 + x] = SrcY[(py + y0 + y) * PadW + px + x0 + x] - mcBuf[(y0 + y) * n + x0 + x];
                    satd += Hadamard8(d);
                }
            return satd + (long)(LambdaSad * (Math.Abs((int)mv.X) + Math.Abs((int)mv.Y)) / 16.0);
        }

        private static long Hadamard8(Span<int> d)
        {
            Span<int> t = stackalloc int[64];
            for (int i = 0; i < 8; i++) H8(d, i * 8, 1, t, i * 8, 1);
            Span<int> o = stackalloc int[64];
            for (int i = 0; i < 8; i++) H8(t, i, 8, o, i, 8);
            long s = 0;
            for (int i = 0; i < 64; i++) s += Math.Abs(o[i]);
            return s >> 2;
        }

        private static void H8(Span<int> a, int off, int st, Span<int> b, int ob, int sb)
        {
            int a0 = a[off], a1 = a[off + st], a2 = a[off + 2 * st], a3 = a[off + 3 * st];
            int a4 = a[off + 4 * st], a5 = a[off + 5 * st], a6 = a[off + 6 * st], a7 = a[off + 7 * st];
            int b0 = a0 + a4, b1 = a1 + a5, b2 = a2 + a6, b3 = a3 + a7, b4 = a0 - a4, b5 = a1 - a5, b6 = a2 - a6, b7 = a3 - a7;
            int c0 = b0 + b2, c1 = b1 + b3, c2 = b0 - b2, c3 = b1 - b3, c4 = b4 + b6, c5 = b5 + b7, c6 = b4 - b6, c7 = b5 - b7;
            b[ob] = c0 + c1; b[ob + sb] = c0 - c1; b[ob + 2 * sb] = c2 + c3; b[ob + 3 * sb] = c2 - c3;
            b[ob + 4 * sb] = c4 + c5; b[ob + 5 * sb] = c4 - c5; b[ob + 6 * sb] = c6 + c7; b[ob + 7 * sb] = c6 - c7;
        }

        // The decoder's unscaled single-reference prediction (Av1Reconstruction.Mc, same-size path) for one plane of a
        // block at 4x4-unit position (bx, by) of luma-sized bw4 x bh4 units... (w, h are the plane block size in px).
        private void McPlane(int pl, int bx4, int by4, int w, int h, Av1MotionVector mv, ushort[] dst, int dstStride)
        {
            int ssH = pl != 0 ? SsX : 0, ssV = pl != 0 ? SsY : 0;
            int hMul = 4 >> ssH, vMul = 4 >> ssV;
            int mvx = mv.X, mvy = mv.Y;
            int mx = mvx & (15 >> (ssH == 0 ? 1 : 0));
            int my = mvy & (15 >> (ssV == 0 ? 1 : 0));
            var plane = pl == 0 ? Ref.Y : pl == 1 ? Ref.U! : Ref.V!;
            int pw = pl == 0 ? Ref.Width : (Ref.Width + ssH) >> ssH;
            int ph = pl == 0 ? Ref.Height : (Ref.Height + ssV) >> ssV;
            int dx = bx4 * hMul + (mvx >> (3 + ssH));
            int dy = by4 * vMul + (mvy >> (3 + ssV));
            ReadOnlySpan<ushort> src;
            int srcStride;
            if (dx < 6 || dy < 6 || dx + w + 7 > pw || dy + h + 7 > ph)
            {
                Av1MotionComp.EmuEdge(w + 7, h + 7, pw, ph, dx - 3, dy - 3, Emu, 192, plane, pw);
                src = Emu;
                srcStride = 192;
            }
            else
            {
                src = plane.AsSpan(pw * (dy - 3) + (dx - 3));
                srcStride = pw;
            }
            Av1MotionComp.Put8Tap(dst, dstStride, src, srcStride, w, h, mx << (ssH == 0 ? 1 : 0), my << (ssV == 0 ? 1 : 0),
                Av1MotionComp.PackedFilterType((int)Av1Filter2d.EightTapRegular));
        }

        // Predicts the block with leaf.Mv, transforms / quantizes the residual (largest transform), and returns
        // D + lambda * R (choosing skip when coding the residual does not pay). Fills leaf levels / skip.
        private double CodeResidual(Leaf leaf, int n, double modeBits)
        {
            int px = leaf.Bx * 4, py = leaf.By * 4;
            int bs = leaf.Bs;
            int ytx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
            var pred = new ushort[n * n];
            McPlane(0, leaf.Bx, leaf.By, n, n, leaf.Mv, pred, n);
            double dSkip = 0, dCoded = 0, coefBits = 0;
            var lumaLv = new List<int[]>();
            ResidualPlane(SrcY, PadW, px, py, n, n, ytx, pred, 0, lumaLv, ref dSkip, ref dCoded, ref coefBits);
            var uLv = new List<int[]>();
            var vLv = new List<int[]>();
            if (!Mono)
            {
                int cw = n >> SsX, ch = n >> SsY;
                int uvtx = Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                var cp = new ushort[cw * ch];
                McPlane(1, leaf.Bx, leaf.By, cw, ch, leaf.Mv, cp, cw);
                ResidualPlane(SrcU, PadW >> SsX, px >> SsX, py >> SsY, cw, ch, uvtx, cp, 1, uLv, ref dSkip, ref dCoded, ref coefBits);
                McPlane(2, leaf.Bx, leaf.By, cw, ch, leaf.Mv, cp, cw);
                ResidualPlane(SrcV, PadW >> SsX, px >> SsX, py >> SsY, cw, ch, uvtx, cp, 2, vLv, ref dSkip, ref dCoded, ref coefBits);
            }
            bool anyCoef = lumaLv.Exists(HasNonZero) || uLv.Exists(HasNonZero) || vLv.Exists(HasNonZero);
            double skipBits1 = Av1CoeffEncode.SymBits(cdf.GetSkipCdf(0), 1), skipBits0 = Av1CoeffEncode.SymBits(cdf.GetSkipCdf(0), 0);
            double jSkip = dSkip + Lambda * (modeBits + skipBits1);
            double jCoded = dCoded + Lambda * (modeBits + skipBits0 + coefBits);
            if (!anyCoef || jSkip <= jCoded)
            {
                leaf.Skip = true;
                return jSkip;
            }
            leaf.LumaLevels = lumaLv; leaf.ULevels = uLv; leaf.VLevels = vLv;
            return jCoded;
        }

        private static bool HasNonZero(int[] a) { foreach (int v in a) if (v != 0) return true; return false; }

        // One plane of a block: per transform block (raster), forward + quantize the residual against pred, and add
        // the skip / coded distortions and the coefficient bits.
        private void ResidualPlane(ushort[] src, int srcW, int px, int py, int w, int h, int tx, ushort[] pred, int plane,
            List<int[]> levelsOut, ref double dSkip, ref double dCoded, ref double coefBits)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int tw = tDim.W * 4, th = tDim.H * 4;
            int rcCount = Math.Min(tw, 32) * Math.Min(th, 32);
            var res = new int[tw * th];
            var recon = new ushort[tw * th];
            int max = (1 << Bd) - 1;
            for (int ty = 0; ty < h; ty += th)
                for (int txo = 0; txo < w; txo += tw)
                {
                    long ss = 0;
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int s = src[(py + ty + y) * srcW + px + txo + x];
                            int p = pred[(ty + y) * w + txo + x];
                            res[y * tw + x] = s - p;
                            ss += (long)(s - p) * (s - p);
                            recon[y * tw + x] = (ushort)p;
                        }
                    dSkip += ss;
                    int[] lv = Av1FwdTransform.ForwardQuantRect(res, tw, th, tx, DcDq, AcDq, rcCount, Av1FwdTransform.FwdTxType.DctDct);
                    levelsOut.Add(lv);
                    if (!HasNonZero(lv)) { dCoded += ss; coefBits += Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, 0, lv, 0, 0, 1, inter: true); continue; }
                    coefBits += Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, 0, lv, 0, 0, 1, inter: true);
                    Reconstruct(lv, tx, recon, tw);
                    long sc = 0;
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int d = src[(py + ty + y) * srcW + px + txo + x] - recon[y * tw + x];
                            sc += (long)d * d;
                        }
                    dCoded += sc;
                }
        }

        private readonly int[] cfBuf = new int[64 * 64];

        // Dequantize + inverse transform on top of the prediction already in recon (the decoder's reconstruction).
        private void Reconstruct(int[] levels, int tx, ushort[] recon, int stride)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int dqShift = Math.Max(0, tDim.Ctx - 2);
            int cfMax = ~(~127 << (Bd == 8 ? 8 : Bd));
            var scan = Av1Tables.Scans[tx];
            Array.Clear(cfBuf);
            int eob = -1;
            for (int i = scan.Length - 1; i >= 0; i--) if (levels[scan[i]] != 0) { eob = i; break; }
            for (int i = 0; i <= eob; i++)
            {
                int rc = scan[i], lvl = levels[rc];
                if (lvl == 0) continue;
                int mag = Math.Abs(lvl), sign = lvl < 0 ? 1 : 0;
                int dq = ((rc == 0 ? DcDq : AcDq) * mag) >> dqShift;
                dq = Math.Min(dq, cfMax + sign);
                cfBuf[rc] = sign != 0 ? -dq : dq;
            }
            if (eob >= 0)
                Av1InvTransform.InvTxfmAdd16(recon, stride, cfBuf, eob, tx, Av1InvTransform.TxShift[tx], Av1TxType.DctDct, Bd);
        }

        // ── refmvs grid: splat / save / restore (dav1d splat_oneref_mv) ──────────────────────────────────────────
        private void Splat(Leaf leaf)
        {
            int bw4 = Av1Tables.BlockDimensions[leaf.Bs, 0], bh4 = Av1Tables.BlockDimensions[leaf.Bs, 1];
            var tmpl = TemplateFor(leaf, InterModeFor(leaf));
            int r0 = (leaf.By & 31) + 5;
            Av1RefMvs.SplatMv(rt.R, r0, in tmpl, leaf.Bx, bw4, bh4);
        }

        // The mode analysis assumes for the refmvs "mf" flags: GLOBALMV for a zero vector, NEWMV otherwise (the flags
        // only steer temporal projection, which these frames do not use; the written mode re-splats the exact value).
        private static int InterModeFor(Leaf leaf) =>
            leaf.Mv.X == 0 && leaf.Mv.Y == 0 ? (int)Av1InterPredMode.GlobalMv : (int)Av1InterPredMode.NewMv;

        private static Av1RefMvsBlock TemplateFor(Leaf leaf, int interMode)
        {
            int bw4 = Av1Tables.BlockDimensions[leaf.Bs, 0], bh4 = Av1Tables.BlockDimensions[leaf.Bs, 1];
            return new Av1RefMvsBlock
            {
                Ref = new Av1RefMvsRefPair { Ref0 = 1, Ref1 = -1 },
                Mv = new Av1RefMvsMvPair { Mv0 = leaf.Mv },
                Bs = (byte)leaf.Bs,
                Mf = (byte)((interMode == (int)Av1InterPredMode.GlobalMv && Math.Min(bw4, bh4) >= 2 ? 1 : 0) |
                            (interMode == (int)Av1InterPredMode.NewMv ? 2 : 0)),
            };
        }

        private Av1RefMvsBlock[][] SaveGrid(int bx, int by, int size4)
        {
            var saved = new Av1RefMvsBlock[size4][];
            int r0 = (by & 31) + 5;
            for (int y = 0; y < size4; y++)
            {
                var row = rt.R[r0 + y];
                saved[y] = row == null ? [] : row.AsSpan(bx, Math.Min(size4, row.Length - bx)).ToArray();
            }
            return saved;
        }

        private void RestoreGrid(Av1RefMvsBlock[][] saved, int bx, int by, int size4)
        {
            int r0 = (by & 31) + 5;
            for (int y = 0; y < size4; y++)
            {
                var row = rt.R[r0 + y];
                if (row != null && saved[y].Length > 0) saved[y].CopyTo(row.AsSpan(bx));
            }
        }

        // ── Writing (mirrors Av1Decode.DecodeSuperblock / DecodeBlock / DecodeBlockInter) ──────────────────────────
        private void Write(Node node)
        {
            int bl = node.Bl, bx = node.Bx, by = node.By, hsz = 16 >> bl;
            bool haveH = Bw > bx + hsz, haveV = Bh > by + hsz;
            if (!haveH && !haveV) { Write(node.Children![0]!); return; }
            int bx8 = (bx & 31) >> 1, by8 = (by & 31) >> 1;
            int partCtx = Av1Decode.GetPartitionCtx(above, left, (Av1BlockLevel)bl, by8, bx8);
            var partCdf = cdf.GetPartitionCdf((Av1BlockLevel)bl, partCtx);
            Av1BlockPartition bp;
            if (haveH && haveV)
            {
                bp = node.Leaf != null ? Av1BlockPartition.None : Av1BlockPartition.Split;
                msac.EncodeSymbolAdapt(partCdf, (int)bp, Av1Tables.PartitionTypeCount[bl]);
                if (node.Leaf != null) WriteBlock(node.Leaf, bl, bp);
                else for (int q = 0; q < 4; q++) if (node.Children![q] is { } c) Write(c);
            }
            else if (haveH)
            {
                bp = Av1BlockPartition.Split;
                msac.EncodeBool(1, Av1Decode.GatherTopPartitionProb(partCdf, (Av1BlockLevel)bl));
                Write(node.Children![0]!);
                Write(node.Children![1]!);
            }
            else
            {
                bp = Av1BlockPartition.Split;
                msac.EncodeBool(1, Av1Decode.GatherLeftPartitionProb(partCdf, (Av1BlockLevel)bl));
                Write(node.Children![0]!);
                Write(node.Children![2]!);
            }
            if (bp != Av1BlockPartition.Split || bl == (int)Av1BlockLevel.Bl8x8)
            {
                int count = 1 << Av1Decode.Ulog2(hsz);
                Av1BlockContextManaged.Fill(above.Partition, bx8, Math.Min(count, above.Partition.Length - bx8), Av1Tables.AboveLeftPartCtx[0, bl, (int)bp]);
                Av1BlockContextManaged.Fill(left.Partition, by8, Math.Min(count, left.Partition.Length - by8), Av1Tables.AboveLeftPartCtx[1, bl, (int)bp]);
            }
        }

        private void WriteBlock(Leaf leaf, int bl, Av1BlockPartition bp)
        {
            int bs = leaf.Bs, bxAbs = leaf.Bx, byAbs = leaf.By;
            int bx4 = bxAbs & 31, by4 = byAbs & 31;
            int bw4 = Av1Tables.BlockDimensions[bs, 0], bh4 = Av1Tables.BlockDimensions[bs, 1];
            int w4 = Math.Min(bw4, Bw - bxAbs), h4 = Math.Min(bh4, Bh - byAbs);
            bool haveLeft = bxAbs > colStart, haveTop = byAbs > rowStart;
            bool hasChroma = !Mono && (bw4 > SsX || (bxAbs & 1) != 0) && (bh4 > SsY || (byAbs & 1) != 0);
            int cbx4 = bx4 >> SsX, cby4 = by4 >> SsY, cbw4 = (bw4 + SsX) >> SsX, cbh4 = (bh4 + SsY) >> SsY;
            var m = cdf.Mode;

            // skip (inter frame), then is_inter, then the single reference LAST.
            int sctx = above.Skip[bx4] + left.Skip[by4];
            msac.EncodeBoolAdapt(cdf.GetSkipCdf(sctx), leaf.Skip ? 1u : 0u);
            int ictx = Av1Decode.GetIntraCtx(above, left, by4, bx4, haveTop, haveLeft);
            msac.EncodeBoolAdapt(cdf.GetIntraCdf(ictx), 1);   // is_inter
            msac.EncodeBoolAdapt(m.Ref[Av1Decode.GetRefCtx(above, left, by4, bx4, haveTop, haveLeft)], 0);
            msac.EncodeBoolAdapt(m.Ref[2 * 3 + Av1Decode.GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], 0);
            msac.EncodeBoolAdapt(m.Ref[3 * 3 + Av1Decode.GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], 0);

            Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
            Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int modeCtx, out _,
                new Av1RefMvsRefPair { Ref0 = 1, Ref1 = -1 }, bs, leaf.Edge, byAbs, bxAbs);

            // The cheapest syntax that reproduces leaf.Mv against this stack.
            var target = leaf.Mv;
            var nearest = stack[0].Mv.Mv0;
            Av1RefMvs.FixMvPrecision(Fh, ref nearest);
            int mode, drl = 0;
            if (target.Equals(nearest)) mode = (int)Av1InterPredMode.NearestMv;
            else
            {
                mode = -1;
                int maxDrl = nCand > 2 ? Math.Min(nCand - 1, 3) : 1;
                for (int d = 1; d <= maxDrl && mode < 0; d++)
                {
                    var near = stack[d].Mv.Mv0;
                    if (d < 2) Av1RefMvs.FixMvPrecision(Fh, ref near);
                    if (target.Equals(near)) { mode = (int)Av1InterPredMode.NearMv; drl = d; }
                }
                if (mode < 0 && target.X == 0 && target.Y == 0) mode = (int)Av1InterPredMode.GlobalMv;
                if (mode < 0) mode = (int)Av1InterPredMode.NewMv;
            }

            if (mode == (int)Av1InterPredMode.NewMv)
            {
                msac.EncodeBoolAdapt(m.NewmvMode[modeCtx & 7], 0);
                // Reference MV: the stack entry (DRL) with the cheapest residual that keeps quarter-pel parity.
                int drls = nCand > 1 ? Math.Min(nCand, 3) : 1;
                double bestBits = double.MaxValue;
                Av1MotionVector refMv = default;
                for (int d = 0; d < drls; d++)
                {
                    var r = stack[nCand > 1 ? d : 0].Mv.Mv0;
                    if (nCand <= 1) Av1RefMvs.FixMvPrecision(Fh, ref r);
                    if (((target.X - r.X) & 1) != 0 || ((target.Y - r.Y) & 1) != 0) continue;
                    double b = MvBits(target, r) + d;
                    if (b < bestBits) { bestBits = b; refMv = r; drl = d; }
                }
                if (bestBits == double.MaxValue) throw new InvalidOperationException("No quarter-pel reference MV for NEWMV.");
                if (nCand > 1)
                {
                    msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 0)], drl >= 1 ? 1u : 0u);
                    if (drl >= 1 && nCand > 2)
                        msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 1)], drl >= 2 ? 1u : 0u);
                }
                WriteMvResidual(target, refMv);
            }
            else
            {
                msac.EncodeBoolAdapt(m.NewmvMode[modeCtx & 7], 1);
                if (mode == (int)Av1InterPredMode.GlobalMv)
                    msac.EncodeBoolAdapt(m.GlobalmvMode[(modeCtx >> 3) & 1], 0);
                else
                {
                    msac.EncodeBoolAdapt(m.GlobalmvMode[(modeCtx >> 3) & 1], 1);
                    if (mode == (int)Av1InterPredMode.NearMv)
                    {
                        msac.EncodeBoolAdapt(m.RefmvMode[(modeCtx >> 4) & 15], 1);
                        if (nCand > 2)
                        {
                            msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 1)], drl >= 2 ? 1u : 0u);
                            if (drl >= 2 && nCand > 3)
                                msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 2)], drl >= 3 ? 1u : 0u);
                        }
                    }
                    else msac.EncodeBoolAdapt(m.RefmvMode[(modeCtx >> 4) & 15], 0);
                }
            }

            // Residual (ReconBlockInter order): per 64x64 chunk, luma transform blocks then U then V.
            if (leaf.Skip)
            {
                FillCoefCtx(above.LCoef, bx4, bw4, 0x40);
                FillCoefCtx(left.LCoef, by4, bh4, 0x40);
                if (hasChroma)
                {
                    FillCoefCtx(above.CCoef0, cbx4, cbw4, 0x40);
                    FillCoefCtx(above.CCoef1, cbx4, cbw4, 0x40);
                    FillCoefCtx(left.CCoef0, cby4, cbh4, 0x40);
                    FillCoefCtx(left.CCoef1, cby4, cbh4, 0x40);
                }
            }
            else
            {
                int ytx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
                int uvtx = Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                ref readonly var yt = ref Av1Tables.TxfmDimensions[ytx];
                ref readonly var uvt = ref Av1Tables.TxfmDimensions[uvtx];
                int cw4 = (w4 + SsX) >> SsX, ch4 = (h4 + SsY) >> SsY;
                int ntw = bw4 / yt.W;   // luma tx blocks per row in the analysis raster (whole block)
                for (int initY = 0; initY < bh4; initY += 16)
                    for (int initX = 0; initX < bw4; initX += 16)
                    {
                        for (int y = initY; y < Math.Min(h4, initY + 16); y += yt.H)
                            for (int x = initX; x < Math.Min(w4, initX + 16); x += yt.W)
                            {
                                int[] lv = leaf.LumaLevels[(y / yt.H) * ntw + x / yt.W];
                                WriteCoefs(ytx, bs, 0, lv, above.LCoef, bx4 + x, left.LCoef, by4 + y,
                                    Math.Min(yt.W, Bw - (bxAbs + x)), Math.Min(yt.H, Bh - (byAbs + y)));
                            }
                        if (hasChroma)
                        {
                            int nctw = cbw4 / uvt.W;
                            for (int pl = 0; pl < 2; pl++)
                            {
                                var ac = pl == 0 ? above.CCoef0 : above.CCoef1;
                                var lc = pl == 0 ? left.CCoef0 : left.CCoef1;
                                var levels = pl == 0 ? leaf.ULevels : leaf.VLevels;
                                for (int y = initY >> SsY; y < Math.Min(ch4, (initY + 16) >> SsY); y += uvt.H)
                                    for (int x = initX >> SsX; x < Math.Min(cw4, (initX + 16) >> SsX); x += uvt.W)
                                    {
                                        int[] lv = levels[(y / uvt.H) * nctw + x / uvt.W];
                                        int tbx = bxAbs + (x << SsX), tby = byAbs + (y << SsY);
                                        WriteCoefs(uvtx, bs, 1, lv, ac, cbx4 + x, lc, cby4 + y,
                                            Math.Min(uvt.W, (Bw - tbx + SsX) >> SsX), Math.Min(uvt.H, (Bh - tby + SsY) >> SsY));
                                    }
                            }
                        }
                    }
            }

            // Context update (DecodeBlock, inter branch).
            for (int i = 0; i < bw4; i++)
            {
                above.Mode[bx4 + i] = (byte)mode; above.CompType[bx4 + i] = 0; above.Ref0[bx4 + i] = 0; above.Ref1[bx4 + i] = -1;
                above.Filter0[bx4 + i] = Av1Tables.FilterDir[0, 0]; above.Filter1[bx4 + i] = Av1Tables.FilterDir[0, 1];
                above.Intra[bx4 + i] = 0; above.Skip[bx4 + i] = leaf.Skip ? (byte)1 : (byte)0; above.SkipMode[bx4 + i] = 0;
                above.SegPred[bx4 + i] = 0; above.PalSz[bx4 + i] = 0; above.TxIntra[bx4 + i] = (sbyte)Av1Tables.BlockDimensions[bs, 2];
            }
            for (int j = 0; j < bh4; j++)
            {
                left.Mode[by4 + j] = (byte)mode; left.CompType[by4 + j] = 0; left.Ref0[by4 + j] = 0; left.Ref1[by4 + j] = -1;
                left.Filter0[by4 + j] = Av1Tables.FilterDir[0, 0]; left.Filter1[by4 + j] = Av1Tables.FilterDir[0, 1];
                left.Intra[by4 + j] = 0; left.Skip[by4 + j] = leaf.Skip ? (byte)1 : (byte)0; left.SkipMode[by4 + j] = 0;
                left.SegPred[by4 + j] = 0; left.PalSz[by4 + j] = 0; left.TxIntra[by4 + j] = (sbyte)Av1Tables.BlockDimensions[bs, 3];
            }
            if (hasChroma)
            {
                for (int i = 0; i < cbw4; i++) above.UvMode[cbx4 + i] = (byte)Av1IntraPredMode.Dc;
                for (int j = 0; j < cbh4; j++) left.UvMode[cby4 + j] = (byte)Av1IntraPredMode.Dc;
            }

            // The decoder's refmvs splat for this block (with the exact mode flags).
            var tmpl = TemplateFor(leaf, mode);
            int r0 = (byAbs & 31) + 5;
            Av1RefMvs.SplatMv(rt.R, r0, in tmpl, bxAbs, bw4, bh4);
        }

        private static void FillCoefCtx(byte[] arr, int off, int n, byte v)
        {
            int cnt = Math.Min(n, arr.Length - off);
            if (cnt > 0) Array.Fill(arr, v, off, cnt);
        }

        private void WriteCoefs(int tx, int bs, int chroma, int[] levels, byte[] a, int aOff, byte[] l, int lOff, int ctw, int cth)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int aLen = Math.Max(1, Math.Min(tDim.W, 32 - aOff)), lLen = Math.Max(1, Math.Min(tDim.H, 32 - lOff));
            int skipCtx = Av1CoeffDecode.GetSkipCtx(in tDim, bs, a.AsSpan(aOff, aLen), l.AsSpan(lOff, lLen), chroma, (int)Layout);
            int signCtx = Av1CoeffDecode.GetDcSignCtx(tx, a.AsSpan(aOff, aLen), l.AsSpan(lOff, lLen));
            Av1CoeffEncode.EncodeCoefs(msac, cdf.Coef, cdf.Mode, tx, chroma, 0, levels, skipCtx, signCtx, 1, inter: true);
            byte cf = CfCtx(levels, tx);
            if (ctw > 0) Av1BlockContextManaged.Fill(a, aOff, Math.Min(ctw, a.Length - aOff), cf);
            if (cth > 0) Av1BlockContextManaged.Fill(l, lOff, Math.Min(cth, l.Length - lOff), cf);
        }

        private static byte CfCtx(int[] levels, int tx)
        {
            var scan = Av1Tables.Scans[tx];
            int eob = -1;
            for (int i = scan.Length - 1; i >= 0; i--) if (levels[scan[i]] != 0) { eob = i; break; }
            if (eob < 0) return 0x40;
            int cul = 0;
            for (int i = 0; i <= eob; i++) cul += Math.Abs(levels[scan[i]]);
            int dcSign = levels[0] == 0 ? 0x40 : (levels[0] < 0 ? 0 : 0x80);
            return (byte)(Math.Min(cul, 63) | dcSign);
        }

        private void WriteMvResidual(Av1MotionVector mv, Av1MotionVector refMv)
        {
            int dy = mv.Y - refMv.Y, dx = mv.X - refMv.X;
            int joint = (dy != 0 ? 2 : 0) | (dx != 0 ? 1 : 0);
            msac.EncodeSymbolAdapt(cdf.Mv.Joint, joint, 3);
            if (dy != 0) WriteMvComponent(cdf.Mv.Comp0, dy);
            if (dx != 0) WriteMvComponent(cdf.Mv.Comp1, dx);
        }

        private void WriteMvComponent(Av1CdfMvComponent c, int diff)
        {
            MvCompSplit(diff, out bool sign, out int cl, out int up, out int fp);
            msac.EncodeBoolAdapt(c.Sign, sign ? 1u : 0u);
            msac.EncodeSymbolAdapt(c.Classes, cl, 10);
            if (cl == 0)
            {
                msac.EncodeBoolAdapt(c.Class0, (uint)up);
                msac.EncodeSymbolAdapt(c.Class0Fp[up], fp, 3);
            }
            else
            {
                for (int i = 0; i < cl; i++) msac.EncodeBoolAdapt(c.ClassN[i], (uint)((up >> i) & 1));
                msac.EncodeSymbolAdapt(c.ClassNFp, fp, 3);
            }
        }
    }
}
