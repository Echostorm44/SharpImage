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

    // TX_MODE_SELECT: inter blocks choose a transform split tree (var-tx, up to two levels below the largest
    // transform) by rate-distortion instead of always using the largest transform.
    internal static bool UseVarTx = true;

    // Inter-frame rate-distortion lambda relative to the still-image one (RdLambdaK).
    internal static double InterLambdaScale = 1.0;

    // Coefficient RDOQ on inter residuals (Av1CoeffEncode.RdoqOptimize), lambda relative to the frame's; 0 = off.
    internal static double InterRdoqScale = 50.0;
    internal static int InterRdoqMaxCoefs = 256;   // RDOQ cost is quadratic in the coefficient count: up to 16x16

    // Intra blocks in inter frames (new content, occlusions): every leaf also tries the intra modes (DC chroma).
    internal static bool UseIntraInInter = true;

    // Motion search: full-pel radius around the best predictor (in luma pixels), then half- and quarter-pel.
    internal static int SearchRange = 24;
    internal static int ExhaustiveRange = 4;

    /// <summary>
    /// Encodes <paramref name="src"/> as an INTER_FRAME predicted from <paramref name="reference"/> (the decoder's
    /// reconstruction of the previous frame, stored in slot 0). The caller holds the <see cref="Av1ObuWriter.LayeredStream"/>
    /// scope with <c>InterFrame</c> set. Returns the OBU_FRAME.
    /// </summary>
    internal static byte[] EncodeFrameObu(Picture src, Picture reference, int bitDepth, Av1PixelLayout layout, int baseQIdx,
        Av1ObuWriter.CdefParams cdef, int lfLevel, (bool FilterIntra, bool EdgeFilter)? seqTools = null)
        => EncodeFrameObu(src, [reference, null, null, null, null, null, null], bitDepth, layout, baseQIdx, cdef, lfLevel, seqTools);

    /// <summary>
    /// As above with several references: <paramref name="refs"/>[i] is the picture reference i (LAST, LAST2, LAST3,
    /// GOLDEN, BWDREF, ALTREF2, ALTREF) reads, or null when blocks should not use it (not coded / a duplicate slot).
    /// Each block picks the reference (and motion vector) with the lowest rate-distortion cost.
    /// </summary>
    internal static byte[] EncodeFrameObu(Picture src, Picture?[] refs, int bitDepth, Av1PixelLayout layout, int baseQIdx,
        Av1ObuWriter.CdefParams cdef, int lfLevel, (bool FilterIntra, bool EdgeFilter)? seqTools = null)
        => EncodeFrameVariants(src, refs, bitDepth, layout, baseQIdx, seqTools)(lfLevel, cdef);

    /// <summary>
    /// Codes the frame's tiles once and returns a builder of the OBU_FRAME for any deblocking level / CDEF parameters
    /// (post-reconstruction filters: the tile data does not depend on them), so the caller can pick them by decoding
    /// the variants. Call the builder inside the same <see cref="Av1ObuWriter.LayeredStream"/> scope state.
    /// </summary>
    internal static Func<int, Av1ObuWriter.CdefParams, byte[]> EncodeFrameVariants(Picture src, Picture?[] refs, int bitDepth,
        Av1PixelLayout layout, int baseQIdx, (bool FilterIntra, bool EdgeFilter)? seqTools = null)
    {
        var reference = refs;
        int w = src.Width, h = src.Height;
        int sbCols = (w + 63) >> 6, sbRows = (h + 63) >> 6;
        bool mono = layout == Av1PixelLayout.I400;
        var enc = new FrameCoder(src, reference, bitDepth, layout, baseQIdx);
        enc.ReferenceSelect = Av1ObuWriter.CurrentReferenceSelect;
        (enc.SeqFilterIntra, enc.SeqEdgeFilter) = seqTools ?? (false, Av1StillImageEncoder.UseIntraEdgeFilter);
        var tl = Av1ObuWriter.TileLayout(sbCols, sbRows);
        var tiles = new List<byte[]>();
        for (int tr = 0; tr < (1 << tl.RowsLog2) && tl.RowStartSb[tr] < sbRows; tr++)
            for (int tc = 0; tc < (1 << tl.ColsLog2) && tl.ColStartSb[tc] < sbCols; tc++)
                tiles.Add(enc.EncodeTile(tl.ColStartSb[tc] << 4, Math.Min(tl.ColStartSb[tc + 1] << 4, enc.Bw),
                    tl.RowStartSb[tr] << 4, Math.Min(tl.RowStartSb[tr + 1] << 4, enc.Bh), tc == 0 ? tr : -1));
        byte[] tg = Av1StillImageEncoder.AssembleTileGroup(tiles);
        return (lfLevel, cdef) =>
        {
            byte[] hdr = Av1ObuWriter.WriteFrameHeaderPayload(baseQIdx, isObuFrame: true, sbCols, sbRows, mono, txModeSelect: UseVarTx,
                cdef, lfLevel, screenContentTools: false, reducedTxSet: true);
            var payload = new byte[hdr.Length + tg.Length];
            hdr.CopyTo(payload, 0);
            tg.CopyTo(payload, hdr.Length);
            return Av1ObuWriter.WrapObu(Av1ObuType.Frame, payload);
        };
    }

    // One analysed leaf block: its size, motion vector and quantized levels per transform block (luma, then U, V,
    // in the decoder's order), or skip when every level is zero.
    private sealed class Leaf
    {
        public int Bx, By, Bs, EdgeIdx;
        public Av1EdgeFlags Edge;
        public Av1MotionVector Mv;
        public int Ref;               // 0 = LAST .. 6 = ALTREF
        public int Ref1 = -1;         // compound: the second reference (average of both predictions), else -1
        public bool Intra;            // an intra block (YMode, DC chroma), levels in LumaTx / ULevels / VLevels
        public int YMode;
        public ushort[]? IntraRecY, IntraRecU, IntraRecV;   // its reconstruction (block-sized, packed)
        public Av1MotionVector Mv1;
        public bool Skip;
        public List<int[]> ULevels = [], VLevels = [];
        // Luma transform blocks (var-tx leaves) keyed by absolute 4x4 position, and the split flags per tree node as
        // the decoder stores them (depth 0 / 1 bit masks indexed yOff * 4 + xOff).
        public Dictionary<(int Bx, int By), int[]> LumaTx = [];
        public int Split0, Split1;
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
        public readonly Picture Src;
        public readonly Picture?[] Refs;
        private Picture Ref;          // the reference the current search / prediction reads
        public readonly int W, H, Bd, SsX, SsY, CW, CH, Bw, Bh, PadW, PadH, BdIdx;
        public readonly Av1PixelLayout Layout;
        public readonly bool Mono;
        public readonly int BaseQIdx, DcDq, AcDq, Qcat;
        public readonly double Lambda, LambdaSad;
        public readonly ushort[] SrcY, SrcU, SrcV;          // padded to PadW x PadH (edge-replicated)
        public readonly Av1RefMvsFrame Rf = new();
        public bool ReferenceSelect;  // the frame header's reference_select (a comp_mode bit precedes each reference)
        public bool SeqFilterIntra, SeqEdgeFilter;
        // base_q_idx 0: a coded-lossless frame (ONLY_4X4 Walsh-Hadamard transforms, no filters); every coded block
        // reconstructs the source exactly and skip is only allowed where the prediction already does.
        private bool Lossless => BaseQIdx == 0;
        // The current frame's reconstruction before loop filtering (what intra prediction reads, as the decoder's
        // planes and pre-filter SB-row edge backups hold it), and the intra modes per 4x4 (luma: -1 inter) / chroma
        // 4x4 (UV mode, DC for inter) for the smooth-neighbour edge flags.
        private ushort[] RecY = null!, RecU = null!, RecV = null!;
        private sbyte[] ModeMap = null!;
        private byte[] UvMap = null!;
        private int CBw;
        public readonly Av1DecoderFrameHeader Fh;
        public readonly ushort[] Emu = new ushort[(128 + 16) * 192];

        public FrameCoder(Picture src, Picture?[] refs, int bitDepth, Av1PixelLayout layout, int baseQIdx)
        {
            Src = src; Refs = refs; Ref = Array.Find(refs, r => r != null) ?? throw new ArgumentException("No reference.");
            Av1MotionComp.McBitDepth = bitDepth; Bd = bitDepth; Layout = layout; BaseQIdx = baseQIdx;
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
            Lambda = Av1StillImageEncoder.RdLambdaK * InterLambdaScale * AcDq * AcDq;
            LambdaSad = Math.Sqrt(Lambda);
            SrcY = Pad(src.Y, W, H, PadW, PadH);
            RecY = new ushort[PadW * PadH];
            RecU = Mono ? [] : new ushort[(PadW >> SsX) * (PadH >> SsY)];
            RecV = Mono ? [] : new ushort[(PadW >> SsX) * (PadH >> SsY)];
            ModeMap = new sbyte[Bw * Bh];
            Array.Fill(ModeMap, (sbyte)-1);
            CBw = (Bw + SsX) >> SsX;
            UvMap = new byte[CBw * ((Bh + SsY) >> SsY)];
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
                Commit(leaf);
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
            Commit(leaf);
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
            bestJ = double.MaxValue;
            Leaf? best = null;
            var single = new Av1MotionVector?[7];
            for (int r = 0; r < 7; r++)
            {
                if (Refs[r] == null) continue;
                Ref = Refs[r]!;
                var leaf = EvaluateLeafRef(bs, bx, by, edge, edgeIdx, r, out double j);
                single[r] = leaf.Mv;
                if (j < bestJ) { bestJ = j; best = leaf; }
            }
            if (UseIntraInInter && !Lossless)
            {
                var il = EvaluateIntra(bs, bx, by, edge, edgeIdx, out double ji);
                if (ji < bestJ) { bestJ = ji; best = il; }
            }
            // Compound (bidirectional): a forward reference averaged with ALTREF.
            if (ReferenceSelect)
                foreach (int r1 in (ReadOnlySpan<int>)[4, 6])
                {
                    if (Refs[r1] == null) continue;
                    foreach (int r0 in (ReadOnlySpan<int>)[0, 3])
                    {
                        if (Refs[r0] == null) continue;
                        var leaf = EvaluateCompound(bs, bx, by, edge, edgeIdx, r0, r1, single[r0]!.Value, single[r1]!.Value, out double j);
                        if (leaf != null && j < bestJ) { bestJ = j; best = leaf; }
                    }
                }
            return best!;
        }

        private Leaf? EvaluateCompound(int bs, int bx, int by, Av1EdgeFlags edge, int edgeIdx, int r0, int r1,
            Av1MotionVector s0, Av1MotionVector s1, out double bestJ)
        {
            int n = 4 * Av1Tables.BlockDimensions[bs, 0];
            Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
            Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int cctx, out _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(r0 + 1), Ref1 = (sbyte)(r1 + 1) }, bs, edge, by, bx);
            double refBits = CompRefBits(r0, r1, bx, by);
            var pairs = new List<(Av1MotionVector, Av1MotionVector)> { (s0, s1), (default, default) };
            for (int i = 0; i < Math.Min(Math.Max(nCand, 2), 4); i++)
            {
                var a = stack[i].Mv.Mv0; var b = stack[i].Mv.Mv1;
                FixQuarter(ref a); FixQuarter(ref b);
                pairs.Add((a, b));
                if (i == 0) { pairs.Add((a, s1)); pairs.Add((s0, b)); }
            }
            bestJ = double.MaxValue;
            Leaf? best = null;
            var seen = new HashSet<(Av1MotionVector, Av1MotionVector)>();
            foreach (var (m0, m1) in pairs)
            {
                if (!seen.Add((m0, m1)) || !MvInRange(m0) || !MvInRange(m1)) continue;
                var (mode, _, bits) = CompSyntax(stack, nCand, cctx, m0, m1);
                if (mode < 0) continue;
                var leaf = new Leaf { Bx = bx, By = by, Bs = bs, EdgeIdx = edgeIdx, Edge = edge, Mv = m0, Mv1 = m1, Ref = r0, Ref1 = r1 };
                double j = CodeResidual(leaf, n, refBits + bits);
                if (j < bestJ) { bestJ = j; best = leaf; }
            }
            return best;
        }

        // Cost of comp_mode = 1 + a bidirectional pair (forward r0 in LAST..GOLDEN, backward r1 in BWDREF..ALTREF).
        private double CompRefBits(int r0, int r1, int bx, int by)
        {
            int bx4 = bx & 31, by4 = by & 31;
            bool haveLeft = bx > colStart, haveTop = by > rowStart;
            var m = cdf.Mode;
            double b = Av1CoeffEncode.SymBits(m.Comp[Av1Decode.GetCompCtx(above, left, by4, bx4, haveTop, haveLeft)], 1)
                + Av1CoeffEncode.SymBits(m.CompDir[Av1Decode.GetCompDirCtx(above, left, by4, bx4, haveTop, haveLeft)], 1)
                + Av1CoeffEncode.SymBits(m.CompFwdRef[0 * 3 + Av1Decode.GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r0 >= 2 ? 1 : 0)
                + (r0 >= 2 ? Av1CoeffEncode.SymBits(m.CompFwdRef[2 * 3 + Av1Decode.GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)], r0 - 2)
                    : Av1CoeffEncode.SymBits(m.CompFwdRef[1 * 3 + Av1Decode.GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], r0))
                + Av1CoeffEncode.SymBits(m.CompBwdRef[0 * 3 + Av1Decode.GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r1 == 6 ? 1 : 0);
            if (r1 != 6) b += Av1CoeffEncode.SymBits(m.CompBwdRef[1 * 3 + Av1Decode.GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], r1 - 4);
            return b;
        }

        // The cheapest compound inter mode (+ DRL index) that reproduces (m0, m1) against the compound stack, and its
        // approximate bits; mode -1 when none can (a NEWMV half needs quarter-pel parity with its reference).
        private (int Mode, int Drl, double Bits) CompSyntax(ReadOnlySpan<Av1RefMvsCandidate> stack, int nCand, int cctx,
            Av1MotionVector m0, Av1MotionVector m1)
        {
            var cm = cdf.Mode.CompInterMode[cctx];
            int bestMode = -1, bestDrl = 0;
            double best = double.MaxValue;
            void Consider(int mode, int drl, double extra)
            {
                double b = Av1CoeffEncode.SymBits(cm, mode) + extra;
                if (b < best) { best = b; bestMode = mode; bestDrl = drl; }
            }
            var st = new Av1MotionVector[8, 2];
            for (int d = 0; d < 8; d++) { st[d, 0] = stack[d].Mv.Mv0; st[d, 1] = stack[d].Mv.Mv1; }
            Av1MotionVector Near(int d, int idx) { var v = st[d, idx]; FixQuarter(ref v); return v; }
            Av1MotionVector Raw(int d, int idx) => st[d, idx];
            bool Parity(Av1MotionVector a, Av1MotionVector r) => ((a.X - r.X) & 1) == 0 && ((a.Y - r.Y) & 1) == 0;
            double NewBits(Av1MotionVector a, Av1MotionVector r) => MvBits(a, r);
            // DRL bits: NEWMV_NEWMV codes drl 0..2 (bits at stack 0, 1); NEAR modes code drl 1..3 (bits at 1, 2).
            double DrlNew(int d) => (nCand > 1 ? 1 : 0) + (d >= 1 && nCand > 2 ? 1 : 0);
            double DrlNear(int d) => (nCand > 2 ? 1 : 0) + (d >= 2 && nCand > 3 ? 1 : 0);
            if (m0.Equals(Near(0, 0)) && m1.Equals(Near(0, 1))) Consider((int)Av1CompInterPredMode.NearestNearest, 0, 0);
            if (m0.X == 0 && m0.Y == 0 && m1.X == 0 && m1.Y == 0) Consider((int)Av1CompInterPredMode.GlobalGlobal, 0, 0);
            int maxNear = nCand > 2 ? Math.Min(nCand - 1, 3) : 1;
            for (int d = 1; d <= maxNear; d++)
            {
                bool n0 = m0.Equals(Near(d, 0)), n1 = m1.Equals(Near(d, 1));
                if (n0 && n1) Consider((int)Av1CompInterPredMode.NearNear, d, DrlNear(d));
                if (n0 && Parity(m1, Raw(d, 1))) Consider((int)Av1CompInterPredMode.NearNew, d, DrlNear(d) + NewBits(m1, Raw(d, 1)));
                if (n1 && Parity(m0, Raw(d, 0))) Consider((int)Av1CompInterPredMode.NewNear, d, DrlNear(d) + NewBits(m0, Raw(d, 0)));
            }
            if (m0.Equals(Near(0, 0)) && Parity(m1, Raw(0, 1)))
                Consider((int)Av1CompInterPredMode.NearestNew, 0, NewBits(m1, Raw(0, 1)));
            if (m1.Equals(Near(0, 1)) && Parity(m0, Raw(0, 0)))
                Consider((int)Av1CompInterPredMode.NewNearest, 0, NewBits(m0, Raw(0, 0)));
            int maxNew = nCand > 1 ? Math.Min(nCand, 3) : 1;
            for (int d = 0; d < maxNew; d++)
                if (Parity(m0, Raw(d, 0)) && Parity(m1, Raw(d, 1)))
                    Consider((int)Av1CompInterPredMode.NewNew, d, DrlNew(d) + NewBits(m0, Raw(d, 0)) + NewBits(m1, Raw(d, 1)));
            return (bestMode, bestDrl, best);
        }

        // Approximate cost of signalling single reference r (read_ref_frames, single branch) with the current contexts.
        private double RefBits(int r, int bx, int by)
        {
            int bx4 = bx & 31, by4 = by & 31;
            bool haveLeft = bx > colStart, haveTop = by > rowStart;
            var m = cdf.Mode;
            double b = Av1CoeffEncode.SymBits(m.Ref[Av1Decode.GetRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r >= 4 ? 1 : 0);
            if (ReferenceSelect) b += Av1CoeffEncode.SymBits(m.Comp[Av1Decode.GetCompCtx(above, left, by4, bx4, haveTop, haveLeft)], 0);
            if (r >= 4)
            {
                b += Av1CoeffEncode.SymBits(m.Ref[1 * 3 + Av1Decode.GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r == 6 ? 1 : 0);
                if (r != 6) b += Av1CoeffEncode.SymBits(m.Ref[5 * 3 + Av1Decode.GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], r - 4);
            }
            else
            {
                b += Av1CoeffEncode.SymBits(m.Ref[2 * 3 + Av1Decode.GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r >= 2 ? 1 : 0);
                b += r >= 2 ? Av1CoeffEncode.SymBits(m.Ref[4 * 3 + Av1Decode.GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)], r - 2)
                    : Av1CoeffEncode.SymBits(m.Ref[3 * 3 + Av1Decode.GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], r);
            }
            return b;
        }

        private Leaf EvaluateLeafRef(int bs, int bx, int by, Av1EdgeFlags edge, int edgeIdx, int refIdx, out double bestJ)
        {
            int n = 4 * Av1Tables.BlockDimensions[bs, 0];
            Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
            Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int modeCtx, out _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(refIdx + 1), Ref1 = -1 }, bs, edge, by, bx);
            double refBits = RefBits(refIdx, bx, by);

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
                var leaf = new Leaf { Bx = bx, By = by, Bs = bs, EdgeIdx = edgeIdx, Edge = edge, Mv = mv, Ref = refIdx };
                double modeBits = refBits + ModeBits(stack, nCand, modeCtx, mv);
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
            // Exhaustive full-pel search close to the zero vector and the best seed (textured / aliased content has a
            // rugged SAD surface the diamond can step over), then diamond steps down to 1 px within SearchRange.
            foreach (var (ex, ey) in new[] { (0, 0), (bmx, bmy) })
                for (int dy = -ExhaustiveRange; dy <= ExhaustiveRange; dy++)
                    for (int dx = -ExhaustiveRange; dx <= ExhaustiveRange; dx++)
                    {
                        int mx = ex + dx, my = ey + dy;
                        long c = SadFull(px, py, n, mx, my) + (long)(LambdaSad * (Math.Abs(mx) + Math.Abs(my)) * 0.5);
                        if (c < bestCost) { bestCost = c; bmx = mx; bmy = my; }
                    }
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
        private void McPlane(int pl, int bx4, int by4, int w, int h, Av1MotionVector mv, ushort[]? dst, int dstStride, short[]? prep = null)
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
            if (prep != null)
                Av1MotionComp.Prep8Tap(prep, src, srcStride, w, h, mx << (ssH == 0 ? 1 : 0), my << (ssV == 0 ? 1 : 0),
                    Av1MotionComp.PackedFilterType((int)Av1Filter2d.EightTapRegular));
            else
                Av1MotionComp.Put8Tap(dst!, dstStride, src, srcStride, w, h, mx << (ssH == 0 ? 1 : 0), my << (ssV == 0 ? 1 : 0),
                    Av1MotionComp.PackedFilterType((int)Av1Filter2d.EightTapRegular));
        }

        private readonly short[] prep0 = new short[64 * 64], prep1 = new short[64 * 64];

        // The decoder's inter prediction of one plane of the leaf (w x h, packed): single reference (put), or the
        // compound average of both references' intermediate-precision predictions (prep + avg).
        private void Predict(Leaf leaf, int pl, int w, int h, ushort[] dst)
        {
            if (leaf.Ref1 < 0)
            {
                Ref = Refs[leaf.Ref]!;
                McPlane(pl, leaf.Bx, leaf.By, w, h, leaf.Mv, dst, w);
                return;
            }
            Ref = Refs[leaf.Ref]!;
            McPlane(pl, leaf.Bx, leaf.By, w, h, leaf.Mv, null, w, prep0);
            Ref = Refs[leaf.Ref1]!;
            McPlane(pl, leaf.Bx, leaf.By, w, h, leaf.Mv1, null, w, prep1);
            Av1MotionComp.Avg(dst, w, prep0, prep1, w, h);
        }

        // Predicts the block with leaf.Mv, transforms / quantizes the residual (largest transform), and returns
        // D + lambda * R (choosing skip when coding the residual does not pay). Fills leaf levels / skip.
        private double CodeResidual(Leaf leaf, int n, double modeBits)
        {
            int px = leaf.Bx * 4, py = leaf.By * 4;
            int bs = leaf.Bs;
            int ytx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
            var pred = new ushort[n * n];
            Predict(leaf, 0, n, n, pred);
            double dSkip = 0, dCoded = 0, coefBits = 0;
            var lumaTx = new Dictionary<(int, int), int[]>();
            int split0 = 0, split1 = 0;
            {
                // One tree per largest transform tiling the block (a single one for blocks up to 64x64).
                ref readonly var mt = ref Av1Tables.TxfmDimensions[ytx];
                int bw4 = Av1Tables.BlockDimensions[bs, 0], bh4 = Av1Tables.BlockDimensions[bs, 1];
                for (int y = 0, yOff = 0; y < bh4; y += mt.H, yOff++)
                    for (int x = 0, xOff = 0; x < bw4; x += mt.W, xOff++)
                    {
                        var node = LumaTree(ytx, 0, xOff, yOff, leaf.Bx + x, leaf.By + y, leaf.Bx, leaf.By, pred, n);
                        dSkip += node.DSkip; dCoded += node.DCoded; coefBits += node.Bits;
                        foreach (var kv in node.Leaves) lumaTx[kv.Key] = kv.Value;
                        split0 |= node.S0; split1 |= node.S1;
                    }
            }
            var uLv = new List<int[]>();
            var vLv = new List<int[]>();
            if (!Mono)
            {
                int cw = n >> SsX, ch = n >> SsY;
                int uvtx = Lossless ? (int)Av1TxSize.Tx4x4 : Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                var cp = new ushort[cw * ch];
                Predict(leaf, 1, cw, ch, cp);
                ResidualPlane(SrcU, PadW >> SsX, px >> SsX, py >> SsY, cw, ch, uvtx, cp, 1, uLv, ref dSkip, ref dCoded, ref coefBits);
                Predict(leaf, 2, cw, ch, cp);
                ResidualPlane(SrcV, PadW >> SsX, px >> SsX, py >> SsY, cw, ch, uvtx, cp, 2, vLv, ref dSkip, ref dCoded, ref coefBits);
            }
            bool anyCoef = lumaTx.Values.Any(HasNonZero) || uLv.Exists(HasNonZero) || vLv.Exists(HasNonZero);
            double skipBits1 = Av1CoeffEncode.SymBits(cdf.GetSkipCdf(0), 1), skipBits0 = Av1CoeffEncode.SymBits(cdf.GetSkipCdf(0), 0);
            double jSkip = dSkip + Lambda * (modeBits + skipBits1);
            double jCoded = dCoded + Lambda * (modeBits + skipBits0 + coefBits);
            if (!anyCoef || (!Lossless && jSkip <= jCoded))
            {
                leaf.Skip = true;
                return jSkip;
            }
            leaf.LumaTx = lumaTx; leaf.Split0 = split0; leaf.Split1 = split1; leaf.ULevels = uLv; leaf.VLevels = vLv;
            return jCoded;
        }

        private static bool HasNonZero(int[] a) { foreach (int v in a) if (v != 0) return true; return false; }

        // Writes the chosen leaf's reconstruction (pre-filter) and intra-mode maps: inter = prediction + residual per
        // transform, intra = the reconstruction its evaluation produced.
        private void Commit(Leaf leaf)
        {
            if (Lossless) return;   // no intra blocks read the reconstruction
            int bw4 = Av1Tables.BlockDimensions[leaf.Bs, 0], bh4 = Av1Tables.BlockDimensions[leaf.Bs, 1];
            int n = bw4 * 4, px = leaf.Bx * 4, py = leaf.By * 4;
            int cw = n >> SsX, ch = n >> SsY, cpx = px >> SsX, cpy = py >> SsY, cStride = PadW >> SsX;
            if (leaf.Intra)
            {
                Blit(leaf.IntraRecY!, n, n, RecY, PadW, px, py);
                if (!Mono) { Blit(leaf.IntraRecU!, cw, ch, RecU, cStride, cpx, cpy); Blit(leaf.IntraRecV!, cw, ch, RecV, cStride, cpx, cpy); }
            }
            else
            {
                var pred = new ushort[n * n];
                Predict(leaf, 0, n, n, pred);
                if (!leaf.Skip)
                {
                    int mtx = Av1Tables.MaxTxfmSizeForBlockSize[leaf.Bs, 0];
                    ref readonly var mt = ref Av1Tables.TxfmDimensions[mtx];
                    for (int y = 0, yOff = 0; y < bh4; y += mt.H, yOff++)
                        for (int x = 0, xOff = 0; x < bw4; x += mt.W, xOff++)
                            CommitTree(leaf, mtx, 0, xOff, yOff, leaf.Bx + x, leaf.By + y, pred, n);
                }
                Blit(pred, n, n, RecY, PadW, px, py);
                if (!Mono)
                {
                    int uvtx = Av1Tables.MaxTxfmSizeForBlockSize[leaf.Bs, (int)Layout];
                    ref readonly var ut = ref Av1Tables.TxfmDimensions[uvtx];
                    int tw = ut.W * 4, th = ut.H * 4, ntw = cw / tw;
                    for (int pl = 1; pl <= 2; pl++)
                    {
                        var cp = new ushort[cw * ch];
                        Predict(leaf, pl, cw, ch, cp);
                        var lvs = pl == 1 ? leaf.ULevels : leaf.VLevels;
                        if (!leaf.Skip)
                            for (int i = 0; i < lvs.Count; i++)
                                if (HasNonZero(lvs[i])) AddResidual(lvs[i], uvtx, cp, cw, (i / ntw) * th, (i % ntw) * tw);
                        Blit(cp, cw, ch, pl == 1 ? RecU : RecV, cStride, cpx, cpy);
                    }
                }
            }
            for (int y = 0; y < bh4 && leaf.By + y < Bh; y++)
                for (int x = 0; x < bw4 && leaf.Bx + x < Bw; x++)
                    ModeMap[(leaf.By + y) * Bw + leaf.Bx + x] = leaf.Intra ? (sbyte)leaf.YMode : (sbyte)-1;
            if (!Mono)
            {
                int cbx = leaf.Bx >> SsX, cby = leaf.By >> SsY, cbw = (bw4 + SsX) >> SsX, cbh = (bh4 + SsY) >> SsY;
                for (int y = 0; y < cbh && (cby + y) * CBw < UvMap.Length; y++)
                    for (int x = 0; x < cbw && cbx + x < CBw; x++)
                        UvMap[(cby + y) * CBw + cbx + x] = (byte)Av1IntraPredMode.Dc;
            }
        }

        // Adds the residual of every coded luma transform of the var-tx tree (read_coef_tree order and positions).
        private void CommitTree(Leaf leaf, int tx, int depth, int xOff, int yOff, int bx, int by, ushort[] pred, int n)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int txw = tDim.W, txh = tDim.H;
            int mask = depth == 0 ? leaf.Split0 : depth == 1 ? leaf.Split1 : 0;
            if (depth < 2 && (mask >> (yOff * 4 + xOff) & 1) != 0)
            {
                int sub = tDim.Sub;
                ref readonly var sd = ref Av1Tables.TxfmDimensions[sub];
                CommitTree(leaf, sub, depth + 1, xOff * 2, yOff * 2, bx, by, pred, n);
                if (txw >= txh && bx + sd.W < Bw) CommitTree(leaf, sub, depth + 1, xOff * 2 + 1, yOff * 2, bx + sd.W, by, pred, n);
                if (txh >= txw && by + sd.H < Bh)
                {
                    CommitTree(leaf, sub, depth + 1, xOff * 2, yOff * 2 + 1, bx, by + sd.H, pred, n);
                    if (txw >= txh && bx + sd.W < Bw) CommitTree(leaf, sub, depth + 1, xOff * 2 + 1, yOff * 2 + 1, bx + sd.W, by + sd.H, pred, n);
                }
                return;
            }
            if (leaf.LumaTx.TryGetValue((bx, by), out var lv) && HasNonZero(lv))
                AddResidual(lv, tx, pred, n, (by - leaf.By) * 4, (bx - leaf.Bx) * 4);
        }

        private void AddResidual(int[] levels, int tx, ushort[] buf, int stride, int y0, int x0)
        {
            ref readonly var td = ref Av1Tables.TxfmDimensions[tx];
            int tw = td.W * 4, th = td.H * 4;
            var tmp = new ushort[tw * th];
            for (int y = 0; y < th; y++) Array.Copy(buf, (y0 + y) * stride + x0, tmp, y * tw, tw);
            Reconstruct(levels, tx, tmp, tw);
            for (int y = 0; y < th; y++) Array.Copy(tmp, y * tw, buf, (y0 + y) * stride + x0, tw);
        }

        private static void Blit(ushort[] src, int w, int h, ushort[] dst, int dstStride, int x0, int y0)
        {
            for (int y = 0; y < h; y++) Array.Copy(src, y * w, dst, (y0 + y) * dstStride + x0, w);
        }

        private static readonly Av1IntraPredMode[] IntraModes =
        [
            Av1IntraPredMode.Dc, Av1IntraPredMode.Vertical, Av1IntraPredMode.Horizontal, Av1IntraPredMode.DiagDownLeft,
            Av1IntraPredMode.DiagDownRight, Av1IntraPredMode.VerticalRight, Av1IntraPredMode.HorizontalDown,
            Av1IntraPredMode.HorizontalUp, Av1IntraPredMode.VerticalLeft, Av1IntraPredMode.Smooth, Av1IntraPredMode.SmoothV,
            Av1IntraPredMode.SmoothH, Av1IntraPredMode.Paeth,
        ];

        private static bool IsSmooth(int m) => m == (int)Av1IntraPredMode.Smooth || m == (int)Av1IntraPredMode.SmoothV || m == (int)Av1IntraPredMode.SmoothH;

        // The decoder's intra prediction of one transform block (Av1Reconstruction.ReconBlockIntra): edges from the
        // pre-filter reconstruction, the smooth-neighbour flags from the blocks above / left of the BLOCK.
        private void PredictIntraTx(int pl, int mode, int bx, int by, int blkX, int blkY, int tx, Av1EdgeFlags edgeFlags,
            ushort[] plane, int stride, ushort[] dst, int dstStride, int dstOff)
        {
            ref readonly var td = ref Av1Tables.TxfmDimensions[tx];
            int ssH = pl != 0 ? SsX : 0, ssV = pl != 0 ? SsY : 0;
            Span<ushort> edge = stackalloc ushort[257];
            int angle = 0;
            int xpos = bx >> ssH, ypos = by >> ssV;
            int m = Av1Reconstruction.PrepareIntraEdges(
                xpos, xpos > (colStart >> ssH), ypos, ypos > (rowStart >> ssV), colEnd >> ssH, rowEnd >> ssV, edgeFlags,
                plane, ypos * 4 * stride + xpos * 4, stride, default, (Av1IntraPredMode)mode, ref angle, td.W, td.H,
                SeqEdgeFilter, edge, 128, Bd);
            int flags = (SeqEdgeFilter ? 1 << 10 : 0);
            if (pl == 0)
            {
                if (blkY > rowStart && ModeMap[(blkY - 1) * Bw + blkX] >= 0 && IsSmooth(ModeMap[(blkY - 1) * Bw + blkX])) flags |= 512;
                if (blkX > colStart && ModeMap[blkY * Bw + blkX - 1] >= 0 && IsSmooth(ModeMap[blkY * Bw + blkX - 1])) flags |= 512;
                Av1IntraPred.Predict16(m, dst.AsSpan(dstOff), dstStride, edge, 128, td.W * 4, td.H * 4, angle | flags,
                    4 * Bw - 4 * bx, 4 * Bh - 4 * by, Bd);
            }
            else
            {
                int cbx = blkX >> SsX, cby = blkY >> SsY;
                if (blkY > rowStart && IsSmooth(UvMap[(cby - 1) * CBw + cbx])) flags |= 512;
                if (blkX > colStart && IsSmooth(UvMap[cby * CBw + cbx - 1])) flags |= 512;
                Av1IntraPred.Predict16(m, dst.AsSpan(dstOff), dstStride, edge, 128, td.W * 4, td.H * 4, angle | flags,
                    (4 * Bw + SsX - 4 * (bx & ~SsX)) >> SsX, (4 * Bh + SsY - 4 * (by & ~SsY)) >> SsY, Bd);
            }
        }

        // An intra leaf (largest transform, DCT_DCT, UV_DC): the best luma mode by rate-distortion, reconstructed like
        // the decoder (the chroma transforms predict from the ones before them).
        private Leaf? EvaluateIntra(int bs, int bx, int by, Av1EdgeFlags edge, int edgeIdx, out double bestJ)
        {
            bestJ = double.MaxValue;
            int bw4 = Av1Tables.BlockDimensions[bs, 0], bh4 = Av1Tables.BlockDimensions[bs, 1];
            int n = bw4 * 4;
            int tx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
            if (Av1Tables.TxfmDimensions[tx].W != bw4) return null;   // one luma transform per block (squares up to 64)
            int w4 = Math.Min(bw4, Bw - bx), h4 = Math.Min(bh4, Bh - by);
            // ReconBlockIntra edge flags for the single luma transform (initX = initY = 0).
            int sbHasTr = 16 < w4 ? 1 : (edge & Av1EdgeFlags.I444TopHasRight) != 0 ? 1 : 0;
            int sbHasBl = 16 < h4 ? 1 : (edge & Av1EdgeFlags.I444LeftHasBottom) != 0 ? 1 : 0;
            int subW4 = Math.Min(w4, 16), subH4 = Math.Min(h4, 16);
            var lumaEdge = ((sbHasTr == 0 && bw4 >= subW4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                           ((sbHasBl == 0 && bh4 >= subH4) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
            var m = cdf.Mode;
            double hdrBits = Av1CoeffEncode.SymBits(cdf.GetIntraCdf(0), 0);
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            if (tDim.Max > (int)Av1TxSize.Tx4x4) hdrBits += Av1CoeffEncode.SymBits(cdf.GetTxSzCdf(tDim.Max - 1, 0), 0);
            var ymCdf = cdf.GetYModeCdf(Av1Tables.YmodeSizeContext[bs]);
            int rc = Math.Min(n, 32) * Math.Min(n, 32);

            // Luma: every mode (angle delta 0), full rate-distortion on the prediction residual.
            int bestMode = 0;
            int[]? bestLv = null;
            ushort[]? bestRec = null;
            double bestLumaJ = double.MaxValue, bestLumaD = 0, bestLumaBits = 0;
            var pred = new ushort[n * n];
            foreach (var mode in IntraModes)
            {
                PredictIntraTx(0, (int)mode, bx, by, bx, by, tx, lumaEdge, RecY, PadW, pred, n, 0);
                double bits = Av1CoeffEncode.SymBits(ymCdf, (int)mode);
                if (mode >= Av1IntraPredMode.Vertical && mode <= Av1IntraPredMode.VerticalLeft)
                    bits += Av1CoeffEncode.SymBits(cdf.GetAngleDeltaCdf((int)mode - (int)Av1IntraPredMode.Vertical), 3);
                if (mode == Av1IntraPredMode.Dc && SeqFilterIntra && n <= 32) bits += Av1CoeffEncode.SymBits(cdf.GetFilterIntraCdf((Av1BlockSize)bs), 0);
                var (lv, rec, d, cb) = IntraResidual(SrcY, PadW, bx * 4, by * 4, n, n, tx, pred, 0, (int)mode, W - bx * 4, H - by * 4);
                double jl = d + Lambda * (bits + cb);
                if (jl < bestLumaJ) { bestLumaJ = jl; bestMode = (int)mode; bestLv = lv; bestRec = rec; bestLumaD = d; bestLumaBits = bits + cb; }
            }

            var leaf = new Leaf { Bx = bx, By = by, Bs = bs, EdgeIdx = edgeIdx, Edge = edge, Intra = true, YMode = bestMode, IntraRecY = bestRec };
            leaf.LumaTx[(bx, by)] = bestLv!;
            double dTot = bestLumaD, bitsTot = hdrBits + bestLumaBits;
            if (!Mono)
            {
                // UV_DC; each chroma transform predicts from the reconstruction written before it.
                bool cflAllowed = ((Av1Tables.CflAllowedMask >> bs) & 1) != 0;
                bitsTot += Av1CoeffEncode.SymBits(cdf.GetUvModeCdf(cflAllowed, bestMode), (int)Av1IntraPredMode.Dc);
                int uvtx = Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                ref readonly var ut = ref Av1Tables.TxfmDimensions[uvtx];
                int cw = n >> SsX, ch = n >> SsY, cStride = PadW >> SsX;
                int cpx = (bx * 4) >> SsX, cpy = (by * 4) >> SsY;
                int cw4 = (w4 + SsX) >> SsX, ch4 = (h4 + SsY) >> SsY;
                int subCw4 = Math.Min(cw4, 16 >> SsX), subCh4 = Math.Min(ch4, 16 >> SsY);
                var layoutBit = (Av1EdgeFlags)((int)Av1EdgeFlags.I420TopHasRight >> ((int)Layout - 1));
                var layoutBitB = (Av1EdgeFlags)((int)Av1EdgeFlags.I420LeftHasBottom >> ((int)Layout - 1));
                int uvSbHasTr = (16 >> SsX) < cw4 ? 1 : (edge & layoutBit) != 0 ? 1 : 0;
                int uvSbHasBl = (16 >> SsY) < ch4 ? 1 : (edge & layoutBitB) != 0 ? 1 : 0;
                for (int pl = 1; pl <= 2; pl++)
                {
                    var plane = pl == 1 ? RecU : RecV;
                    var srcP = pl == 1 ? SrcU : SrcV;
                    var levels = new List<int[]>();
                    for (int yy = 0; yy < subCh4; yy += ut.H)
                        for (int xx = 0; xx < subCw4; xx += ut.W)
                        {
                            var le = (((yy > 0 || uvSbHasTr == 0) && xx + ut.W >= subCw4) ? 0 : Av1EdgeFlags.I444TopHasRight) |
                                     ((xx > 0 || (uvSbHasBl == 0 && yy + ut.H >= subCh4)) ? 0 : Av1EdgeFlags.I444LeftHasBottom);
                            int tbx = bx + (xx << SsX), tby = by + (yy << SsY);
                            int ox = cpx + xx * 4, oy = cpy + yy * 4;
                            var tp = new ushort[ut.W * 4 * ut.H * 4];
                            PredictIntraTx(pl, (int)Av1IntraPredMode.Dc, tbx, tby, bx, by, uvtx, le, plane, cStride, tp, ut.W * 4, 0);
                            var (lv, rec, d, cb) = IntraResidual(srcP, cStride, ox, oy, ut.W * 4, ut.H * 4, uvtx, tp, pl, 0,
                                CW - ox, CH - oy);
                            Blit(rec, ut.W * 4, ut.H * 4, plane, cStride, ox, oy);   // later transforms predict from it
                            levels.Add(lv);
                            dTot += d; bitsTot += cb;
                        }
                    // Transforms past the visible area (not coded) keep their prediction-free zero levels.
                    int nctw = cw / (ut.W * 4), ncth = ch / (ut.H * 4);
                    var full = new List<int[]>();
                    int k = 0;
                    for (int yy = 0; yy < ncth; yy++)
                        for (int xx = 0; xx < nctw; xx++)
                            full.Add(yy * ut.H < subCh4 && xx * ut.W < subCw4 ? levels[k++] : new int[Math.Min(ut.W * 4, 32) * Math.Min(ut.H * 4, 32)]);
                    var recC = new ushort[cw * ch];
                    for (int y = 0; y < ch; y++) Array.Copy(plane, (cpy + y) * cStride + cpx, recC, y * cw, cw);
                    if (pl == 1) { leaf.ULevels = full; leaf.IntraRecU = recC; } else { leaf.VLevels = full; leaf.IntraRecV = recC; }
                }
            }
            bool any = leaf.LumaTx.Values.Any(HasNonZero) || leaf.ULevels.Exists(HasNonZero) || leaf.VLevels.Exists(HasNonZero);
            leaf.Skip = !any;
            double skipBits = Av1CoeffEncode.SymBits(cdf.GetSkipCdf(0), leaf.Skip ? 1 : 0);
            bestJ = dTot + Lambda * (bitsTot + skipBits);
            return leaf;
        }

        // One intra transform block: residual against pred, quantize (DCT_DCT, RDOQ up to 16x16), reconstruct.
        // Distortion over the visible (visW x visH) part.
        private (int[] Levels, ushort[] Rec, double D, double Bits) IntraResidual(ushort[] src, int srcW, int px, int py, int tw, int th,
            int tx, ushort[] pred, int plane, int yMode, int visW, int visH)
        {
            int rcCount = Math.Min(tw, 32) * Math.Min(th, 32);
            var res = new int[tw * th];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++) res[y * tw + x] = src[(py + y) * srcW + px + x] - pred[y * tw + x];
            var qf = InterRdoqScale > 0 && rcCount <= InterRdoqMaxCoefs ? new double[rcCount] : null;
            int[] lv = Av1FwdTransform.ForwardQuantRect(res, tw, th, tx, DcDq, AcDq, rcCount, Av1FwdTransform.FwdTxType.DctDct, qf);
            if (qf != null && HasNonZero(lv))
                Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, yMode, lv, qf, DcDq, AcDq, 0, 0, 1, InterRdoqScale * Lambda);
            double bits = Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, yMode, lv, 0, 0, 1);
            var rec = (ushort[])pred.Clone();
            if (HasNonZero(lv)) Reconstruct(lv, tx, rec, tw);
            double d = 0;
            for (int y = 0; y < Math.Min(th, visH); y++)
                for (int x = 0; x < Math.Min(tw, visW); x++) { double e = src[(py + y) * srcW + px + x] - rec[y * tw + x]; d += e * e; }
            return (lv, rec, d, bits);
        }

        private sealed class TxNode
        {
            public double DSkip, DCoded, Bits;
            public Dictionary<(int, int), int[]> Leaves = [];
            public int S0, S1;
        }

        // The rate-distortion best transform tree for luma transform `tx` at absolute 4x4 position (bx, by) of the
        // block at (blkX, blkY): coded whole, or (below depth 2, above 4x4) split into its four sub-transforms that
        // start inside the frame (dav1d read_tx_tree / read_coef_tree); an 8x8 split codes four 4x4 transforms.
        private TxNode LumaTree(int tx, int depth, int xOff, int yOff, int bx, int by, int blkX, int blkY, ushort[] pred, int predW)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            if (Lossless)
            {
                var ll = new TxNode();
                double ds = 0, dc = 0, bits = 0;
                var lvs = new List<int[]>();
                ResidualPlane(SrcY, PadW, bx * 4, by * 4, tDim.W * 4, tDim.H * 4, tx, pred, 0, lvs, ref ds, ref dc, ref bits,
                    ((by - blkY) * 4) * predW + (bx - blkX) * 4, predW);
                ll.DSkip = ds; ll.Bits = bits;
                int k = 0;
                for (int y = 0; y < tDim.H; y++)
                    for (int x = 0; x < tDim.W; x++) ll.Leaves[(bx + x, by + y)] = lvs[k++];
                return ll;
            }
            bool canSplit = UseVarTx && depth < 2 && tx > (int)Av1TxSize.Tx4x4;
            int cat = 2 * ((int)Av1TxSize.Tx64x64 - tDim.Max) - depth;
            var whole = new TxNode();
            {
                double ds = 0, dc = 0, bits = 0;
                var lv = new List<int[]>();
                ResidualPlane(SrcY, PadW, bx * 4, by * 4, tDim.W * 4, tDim.H * 4, tx, pred, 0, lv, ref ds, ref dc, ref bits,
                    ((by - blkY) * 4) * predW + (bx - blkX) * 4, predW);
                whole.DSkip = ds; whole.DCoded = dc;
                whole.Bits = bits + (canSplit ? Av1CoeffEncode.SymBits(cdf.GetTxPartCdf(cat, 1), 0) : 0);
                whole.Leaves[(bx, by)] = lv[0];
            }
            if (!canSplit) return whole;
            var split = new TxNode { Bits = Av1CoeffEncode.SymBits(cdf.GetTxPartCdf(cat, 1), 1) };
            if (depth == 0) split.S0 |= 1 << (yOff * 4 + xOff); else split.S1 |= 1 << (yOff * 4 + xOff);
            int sub = tDim.Sub;
            ref readonly var sd = ref Av1Tables.TxfmDimensions[sub];
            int txw = tDim.Lw, txh = tDim.Lh;
            bool deeper = tDim.Max > (int)Av1TxSize.Tx8x8;
            void Child(int cx, int cy, int dx, int dy)
            {
                // Below an 8x8 split there are no more symbols: the 4x4 children are plain leaves.
                var c = deeper
                    ? LumaTree(sub, depth + 1, xOff * 2 + dx, yOff * 2 + dy, cx, cy, blkX, blkY, pred, predW)
                    : LumaLeafOnly(sub, cx, cy, blkX, blkY, pred, predW);
                split.DSkip += c.DSkip; split.DCoded += c.DCoded; split.Bits += c.Bits;
                foreach (var kv in c.Leaves) split.Leaves[kv.Key] = kv.Value;
                split.S0 |= c.S0; split.S1 |= c.S1;
            }
            Child(bx, by, 0, 0);
            if (txw >= txh && bx + sd.W < Bw) Child(bx + sd.W, by, 1, 0);
            if (txh >= txw && by + sd.H < Bh)
            {
                Child(bx, by + sd.H, 0, 1);
                if (txw >= txh && bx + sd.W < Bw) Child(bx + sd.W, by + sd.H, 1, 1);
            }
            return whole.DCoded + Lambda * whole.Bits <= split.DCoded + Lambda * split.Bits ? whole : split;
        }

        private TxNode LumaLeafOnly(int tx, int bx, int by, int blkX, int blkY, ushort[] pred, int predW)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            var node = new TxNode();
            double ds = 0, dc = 0, bits = 0;
            var lv = new List<int[]>();
            ResidualPlane(SrcY, PadW, bx * 4, by * 4, tDim.W * 4, tDim.H * 4, tx, pred, 0, lv, ref ds, ref dc, ref bits,
                ((by - blkY) * 4) * predW + (bx - blkX) * 4, predW);
            node.DSkip = ds; node.DCoded = dc; node.Bits = bits;
            node.Leaves[(bx, by)] = lv[0];
            return node;
        }

        // One plane of a block: per transform block (raster), forward + quantize the residual against pred, and add
        // the skip / coded distortions and the coefficient bits.
        private void ResidualPlane(ushort[] src, int srcW, int px, int py, int w, int h, int tx, ushort[] pred, int plane,
            List<int[]> levelsOut, ref double dSkip, ref double dCoded, ref double coefBits, int predOff = 0, int predStride = -1)
        {
            if (predStride < 0) predStride = w;
            if (Lossless)
            {
                Span<int> r4 = stackalloc int[16];
                for (int ty = 0; ty < h; ty += 4)
                    for (int txo = 0; txo < w; txo += 4)
                    {
                        long ss = 0;
                        int vw = (plane == 0 ? W : CW) - (px + txo), vh = (plane == 0 ? H : CH) - (py + ty);
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                            {
                                int d = src[(py + ty + y) * srcW + px + txo + x] - pred[predOff + (ty + y) * predStride + txo + x];
                                r4[y * 4 + x] = d;
                                if (x < vw && y < vh) ss += (long)d * d;
                            }
                        var lv = new int[16];
                        Av1LosslessEncoder.Fwht4(r4, lv);
                        levelsOut.Add(lv);
                        dSkip += ss;   // coded: exact
                        coefBits += Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, (int)Av1TxSize.Tx4x4, plane > 0 ? 1 : 0, 0, lv, 0, 0, 1);
                    }
                return;
            }
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int tw = tDim.W * 4, th = tDim.H * 4;
            int rcCount = Math.Min(tw, 32) * Math.Min(th, 32);
            var res = new int[tw * th];
            var recon = new ushort[tw * th];
            int max = (1 << Bd) - 1;
            // Distortion counts in-frame samples only (the padding past the frame edge is never displayed).
            int visW = plane == 0 ? W : CW, visH = plane == 0 ? H : CH;
            for (int ty = 0; ty < h; ty += th)
                for (int txo = 0; txo < w; txo += tw)
                {
                    long ss = 0;
                    int vw = visW - (px + txo), vh = visH - (py + ty);
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int s = src[(py + ty + y) * srcW + px + txo + x];
                            int p = pred[predOff + (ty + y) * predStride + txo + x];
                            res[y * tw + x] = s - p;
                            if (x < vw && y < vh) ss += (long)(s - p) * (s - p);
                            recon[y * tw + x] = (ushort)p;
                        }
                    dSkip += ss;
                    var qf = InterRdoqScale > 0 && rcCount <= InterRdoqMaxCoefs && !Lossless ? new double[rcCount] : null;
                    int[] lv = Av1FwdTransform.ForwardQuantRect(res, tw, th, tx, DcDq, AcDq, rcCount, Av1FwdTransform.FwdTxType.DctDct, qf);
                    if (qf != null && HasNonZero(lv))
                        Av1CoeffEncode.RdoqOptimize(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, 0, lv, qf, DcDq, AcDq, 0, 0, 1,
                            InterRdoqScale * Lambda);
                    levelsOut.Add(lv);
                    if (!HasNonZero(lv)) { dCoded += ss; coefBits += Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, 0, lv, 0, 0, 1, inter: true); continue; }
                    coefBits += Av1CoeffEncode.EstimateCoefBits(cdf.Coef, cdf.Mode, tx, plane > 0 ? 1 : 0, 0, lv, 0, 0, 1, inter: true);
                    Reconstruct(lv, tx, recon, tw);
                    long sc = 0;
                    for (int y = 0; y < th; y++)
                        for (int x = 0; x < tw; x++)
                        {
                            int d = src[(py + ty + y) * srcW + px + txo + x] - recon[y * tw + x];
                            if (x < vw && y < vh) sc += (long)d * d;
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
        private static int InterModeFor(Leaf leaf) => leaf.Ref1 >= 0
            ? (leaf.Mv.X == 0 && leaf.Mv.Y == 0 && leaf.Mv1.X == 0 && leaf.Mv1.Y == 0 ? (int)Av1CompInterPredMode.GlobalGlobal : (int)Av1CompInterPredMode.NewNew)
            : leaf.Mv.X == 0 && leaf.Mv.Y == 0 ? (int)Av1InterPredMode.GlobalMv : (int)Av1InterPredMode.NewMv;

        private static Av1RefMvsBlock TemplateFor(Leaf leaf, int interMode)
        {
            int bw4 = Av1Tables.BlockDimensions[leaf.Bs, 0], bh4 = Av1Tables.BlockDimensions[leaf.Bs, 1];
            if (leaf.Intra)   // the intra-ref marker (DecodeBlock)
                return new Av1RefMvsBlock
                {
                    Ref = new Av1RefMvsRefPair { Ref0 = 0, Ref1 = -1 },
                    Mv = new Av1RefMvsMvPair { Mv0 = new Av1MotionVector { Raw = 0x80008000 } },
                    Bs = (byte)leaf.Bs,
                    Mf = 0,
                };
            if (leaf.Ref1 >= 0)   // dav1d splat_tworef_mv
                return new Av1RefMvsBlock
                {
                    Ref = new Av1RefMvsRefPair { Ref0 = (sbyte)(leaf.Ref + 1), Ref1 = (sbyte)(leaf.Ref1 + 1) },
                    Mv = new Av1RefMvsMvPair { Mv0 = leaf.Mv, Mv1 = leaf.Mv1 },
                    Bs = (byte)leaf.Bs,
                    Mf = (byte)((interMode == (int)Av1CompInterPredMode.GlobalGlobal ? 1 : 0) | (((1 << interMode) & 0xbc) != 0 ? 2 : 0)),
                };
            return new Av1RefMvsBlock
            {
                Ref = new Av1RefMvsRefPair { Ref0 = (sbyte)(leaf.Ref + 1), Ref1 = -1 },
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

            if (leaf.Intra)
            {
                WriteIntraBlock(leaf, bs, bx4, by4, bxAbs, byAbs, bw4, bh4, w4, h4, haveTop, haveLeft, hasChroma, cbx4, cby4, cbw4, cbh4);
                return;
            }
            // skip (inter frame), then is_inter, then the single reference (read_ref_frames).
            int sctx = above.Skip[bx4] + left.Skip[by4];
            msac.EncodeBoolAdapt(cdf.GetSkipCdf(sctx), leaf.Skip ? 1u : 0u);
            int ictx = Av1Decode.GetIntraCtx(above, left, by4, bx4, haveTop, haveLeft);
            msac.EncodeBoolAdapt(cdf.GetIntraCdf(ictx), 1);   // is_inter
            int mode;
            if (leaf.Ref1 >= 0) mode = WriteCompound(leaf, bs, bx4, by4, haveTop, haveLeft);
            else
            {
                if (ReferenceSelect && Math.Min(bw4, bh4) > 1)
                    msac.EncodeBoolAdapt(m.Comp[Av1Decode.GetCompCtx(above, left, by4, bx4, haveTop, haveLeft)], 0);   // single
                int r = leaf.Ref;
                msac.EncodeBoolAdapt(m.Ref[Av1Decode.GetRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r >= 4 ? 1u : 0u);
                if (r >= 4)
                {
                    msac.EncodeBoolAdapt(m.Ref[1 * 3 + Av1Decode.GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r == 6 ? 1u : 0u);
                    if (r != 6) msac.EncodeBoolAdapt(m.Ref[5 * 3 + Av1Decode.GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)(r - 4));
                }
                else
                {
                    msac.EncodeBoolAdapt(m.Ref[2 * 3 + Av1Decode.GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r >= 2 ? 1u : 0u);
                    if (r >= 2) msac.EncodeBoolAdapt(m.Ref[4 * 3 + Av1Decode.GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)(r - 2));
                    else msac.EncodeBoolAdapt(m.Ref[3 * 3 + Av1Decode.GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)r);
                }

                Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
                Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int modeCtx, out _,
                    new Av1RefMvsRefPair { Ref0 = (sbyte)(r + 1), Ref1 = -1 }, bs, leaf.Edge, byAbs, bxAbs);

                // The cheapest syntax that reproduces leaf.Mv against this stack.
                var target = leaf.Mv;
                var nearest = stack[0].Mv.Mv0;
                Av1RefMvs.FixMvPrecision(Fh, ref nearest);
                int drl = 0;
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
                        var rm = stack[nCand > 1 ? d : 0].Mv.Mv0;
                        if (nCand <= 1) Av1RefMvs.FixMvPrecision(Fh, ref rm);
                        if (((target.X - rm.X) & 1) != 0 || ((target.Y - rm.Y) & 1) != 0) continue;
                        double b = MvBits(target, rm) + d;
                        if (b < bestBits) { bestBits = b; refMv = rm; drl = d; }
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
            }

            // Transform size (read_vartx_tree): the split tree for a coded block, the block size for a skipped one.
            int maxTx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
            if (UseVarTx && !Lossless)
            {
                if (leaf.Skip)
                {
                    Av1BlockContextManaged.Fill(above.Tx, bx4, bw4, (sbyte)Av1Tables.BlockDimensions[bs, 2]);
                    Av1BlockContextManaged.Fill(left.Tx, by4, bh4, (sbyte)Av1Tables.BlockDimensions[bs, 3]);
                }
                else
                {
                    ref readonly var mt = ref Av1Tables.TxfmDimensions[maxTx];
                    for (int y = 0, yOff = 0; y < bh4; y += mt.H, yOff++)
                        for (int x = 0, xOff = 0; x < bw4; x += mt.W, xOff++)
                            WriteTxTree(leaf, maxTx, 0, xOff, yOff, bxAbs + x, byAbs + y);
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
                int ytx = Lossless ? (int)Av1TxSize.Tx4x4 : Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
                int uvtx = Lossless ? (int)Av1TxSize.Tx4x4 : Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                ref readonly var yt = ref Av1Tables.TxfmDimensions[ytx];
                ref readonly var uvt = ref Av1Tables.TxfmDimensions[uvtx];
                int cw4 = (w4 + SsX) >> SsX, ch4 = (h4 + SsY) >> SsY;
                int ntw = bw4 / yt.W;   // luma tx blocks per row in the analysis raster (whole block)
                for (int initY = 0; initY < bh4; initY += 16)
                    for (int initX = 0; initX < bw4; initX += 16)
                    {
                        int yOffC = initY != 0 ? 1 : 0;
                        for (int y = initY; y < Math.Min(h4, initY + 16); y += yt.H, yOffC++)
                        {
                            int xOffC = initX != 0 ? 1 : 0;
                            for (int x = initX; x < Math.Min(w4, initX + 16); x += yt.W, xOffC++)
                                WriteCoefTree(leaf, bs, ytx, 0, xOffC, yOffC, bxAbs + x, byAbs + y);
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
            byte compType = leaf.Ref1 >= 0 ? (byte)Av1CompInterType.Average : (byte)0;
            for (int i = 0; i < bw4; i++)
            {
                above.Mode[bx4 + i] = (byte)mode; above.CompType[bx4 + i] = compType; above.Ref0[bx4 + i] = (sbyte)leaf.Ref; above.Ref1[bx4 + i] = (sbyte)leaf.Ref1;
                above.Filter0[bx4 + i] = Av1Tables.FilterDir[0, 0]; above.Filter1[bx4 + i] = Av1Tables.FilterDir[0, 1];
                above.Intra[bx4 + i] = 0; above.Skip[bx4 + i] = leaf.Skip ? (byte)1 : (byte)0; above.SkipMode[bx4 + i] = 0;
                above.SegPred[bx4 + i] = 0; above.PalSz[bx4 + i] = 0; above.TxIntra[bx4 + i] = (sbyte)Av1Tables.BlockDimensions[bs, 2];
            }
            for (int j = 0; j < bh4; j++)
            {
                left.Mode[by4 + j] = (byte)mode; left.CompType[by4 + j] = compType; left.Ref0[by4 + j] = (sbyte)leaf.Ref; left.Ref1[by4 + j] = (sbyte)leaf.Ref1;
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

        // comp_mode = 1, the bidirectional reference pair, the compound inter mode + DRL and the NEWMV halves
        // (read_ref_frames / DecodeBlockInter compound branch). Returns the compound mode (the context's Mode byte).
        private int WriteCompound(Leaf leaf, int bs, int bx4, int by4, bool haveTop, bool haveLeft)
        {
            var m = cdf.Mode;
            int r0 = leaf.Ref, r1 = leaf.Ref1;
            msac.EncodeBoolAdapt(m.Comp[Av1Decode.GetCompCtx(above, left, by4, bx4, haveTop, haveLeft)], 1);
            msac.EncodeBoolAdapt(m.CompDir[Av1Decode.GetCompDirCtx(above, left, by4, bx4, haveTop, haveLeft)], 1);   // bidirectional
            msac.EncodeBoolAdapt(m.CompFwdRef[0 * 3 + Av1Decode.GetFwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r0 >= 2 ? 1u : 0u);
            if (r0 >= 2) msac.EncodeBoolAdapt(m.CompFwdRef[2 * 3 + Av1Decode.GetFwdRef2Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)(r0 - 2));
            else msac.EncodeBoolAdapt(m.CompFwdRef[1 * 3 + Av1Decode.GetFwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)r0);
            msac.EncodeBoolAdapt(m.CompBwdRef[0 * 3 + Av1Decode.GetBwdRefCtx(above, left, by4, bx4, haveTop, haveLeft)], r1 == 6 ? 1u : 0u);
            if (r1 != 6) msac.EncodeBoolAdapt(m.CompBwdRef[1 * 3 + Av1Decode.GetBwdRef1Ctx(above, left, by4, bx4, haveTop, haveLeft)], (uint)(r1 - 4));

            Span<Av1RefMvsCandidate> stack = stackalloc Av1RefMvsCandidate[8];
            Av1RefMvs.FindRefMvs(rt, stack, out int nCand, out int cctx, out _,
                new Av1RefMvsRefPair { Ref0 = (sbyte)(r0 + 1), Ref1 = (sbyte)(r1 + 1) }, bs, leaf.Edge, leaf.By, leaf.Bx);
            var (mode, drl, _) = CompSyntax(stack, nCand, cctx, leaf.Mv, leaf.Mv1);
            if (mode < 0) throw new InvalidOperationException("No compound syntax for the chosen motion vectors.");
            msac.EncodeSymbolAdapt(m.CompInterMode[cctx], mode, (int)Av1CompInterPredMode.Count - 1);
            int im0 = Av1Tables.CompInterPredModes[mode, 0], im1 = Av1Tables.CompInterPredModes[mode, 1];
            if (mode == (int)Av1CompInterPredMode.NewNew)
            {
                if (nCand > 1)
                {
                    msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 0)], drl >= 1 ? 1u : 0u);
                    if (drl >= 1 && nCand > 2) msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 1)], drl >= 2 ? 1u : 0u);
                }
            }
            else if (im0 == (int)Av1InterPredMode.NearMv || im1 == (int)Av1InterPredMode.NearMv)
            {
                if (nCand > 2)
                {
                    msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 1)], drl >= 2 ? 1u : 0u);
                    if (drl >= 2 && nCand > 3) msac.EncodeBoolAdapt(m.DrlBit[Av1RefMvs.GetDrlContext(stack, 2)], drl >= 3 ? 1u : 0u);
                }
            }
            if (im0 == (int)Av1InterPredMode.NewMv) WriteMvResidual(leaf.Mv, stack[drl].Mv.Mv0);
            if (im1 == (int)Av1InterPredMode.NewMv) WriteMvResidual(leaf.Mv1, stack[drl].Mv.Mv1);
            return mode;
        }

        // An intra block of an inter frame (DecodeBlock intra branch + DecodeBlockIntra + ReconBlockIntra order +
        // UpdateIntraBlockContext): skip, is_inter = 0, y mode, angle delta, UV_DC, filter intra off, tx depth 0, then
        // per 64x64 chunk the luma transform and the chroma transforms.
        private void WriteIntraBlock(Leaf leaf, int bs, int bx4, int by4, int bxAbs, int byAbs, int bw4, int bh4, int w4, int h4,
            bool haveTop, bool haveLeft, bool hasChroma, int cbx4, int cby4, int cbw4, int cbh4)
        {
            var m = cdf.Mode;
            msac.EncodeBoolAdapt(cdf.GetSkipCdf(above.Skip[bx4] + left.Skip[by4]), leaf.Skip ? 1u : 0u);
            msac.EncodeBoolAdapt(cdf.GetIntraCdf(Av1Decode.GetIntraCtx(above, left, by4, bx4, haveTop, haveLeft)), 0);   // is_inter = 0
            int mode = leaf.YMode;
            msac.EncodeSymbolAdapt(cdf.GetYModeCdf(Av1Tables.YmodeSizeContext[bs]), mode, Av1Constants.NumIntraPredModes - 1);
            if (mode >= (int)Av1IntraPredMode.Vertical && mode <= (int)Av1IntraPredMode.VerticalLeft)
                msac.EncodeSymbolAdapt(cdf.GetAngleDeltaCdf(mode - (int)Av1IntraPredMode.Vertical), 3, 6);
            if (hasChroma)
            {
                bool cflAllowed = ((Av1Tables.CflAllowedMask >> bs) & 1) != 0;
                msac.EncodeSymbolAdapt(cdf.GetUvModeCdf(cflAllowed, mode), (int)Av1IntraPredMode.Dc,
                    Av1Constants.NumUvIntraPredModes - 1 - (cflAllowed ? 0 : 1));
            }
            if (mode == (int)Av1IntraPredMode.Dc && SeqFilterIntra && Math.Max(Av1Tables.BlockDimensions[bs, 2], Av1Tables.BlockDimensions[bs, 3]) <= 3)
                msac.EncodeBoolAdapt(cdf.GetFilterIntraCdf((Av1BlockSize)bs), 0);
            int tx = Av1Tables.MaxTxfmSizeForBlockSize[bs, 0];
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            if (UseVarTx && tDim.Max > (int)Av1TxSize.Tx4x4)
                msac.EncodeSymbolAdapt(cdf.GetTxSzCdf(tDim.Max - 1, Av1Decode.GetTxCtx(above, left, in tDim, by4, bx4)), 0, Math.Min((int)tDim.Max, 2));

            // Coefficients: the single luma transform, then each chroma plane's transforms.
            if (leaf.Skip)
            {
                FillCoefCtx(above.LCoef, bx4, bw4, 0x40);
                FillCoefCtx(left.LCoef, by4, bh4, 0x40);
                if (hasChroma)
                {
                    FillCoefCtx(above.CCoef0, cbx4, cbw4, 0x40); FillCoefCtx(above.CCoef1, cbx4, cbw4, 0x40);
                    FillCoefCtx(left.CCoef0, cby4, cbh4, 0x40); FillCoefCtx(left.CCoef1, cby4, cbh4, 0x40);
                }
            }
            else
            {
                WriteCoefs(tx, bs, 0, leaf.LumaTx[(bxAbs, byAbs)], above.LCoef, bx4, left.LCoef, by4,
                    Math.Min(tDim.W, Bw - bxAbs), Math.Min(tDim.H, Bh - byAbs), mode);
                if (hasChroma)
                {
                    int uvtx = Av1Tables.MaxTxfmSizeForBlockSize[bs, (int)Layout];
                    ref readonly var ut = ref Av1Tables.TxfmDimensions[uvtx];
                    int cw4 = (w4 + SsX) >> SsX, ch4 = (h4 + SsY) >> SsY;
                    int subCw4 = Math.Min(cw4, 16 >> SsX), subCh4 = Math.Min(ch4, 16 >> SsY);
                    int nctw = cbw4 / ut.W;
                    for (int pl = 0; pl < 2; pl++)
                    {
                        var ac = pl == 0 ? above.CCoef0 : above.CCoef1;
                        var lc = pl == 0 ? left.CCoef0 : left.CCoef1;
                        var levels = pl == 0 ? leaf.ULevels : leaf.VLevels;
                        for (int y = 0; y < subCh4; y += ut.H)
                            for (int x = 0; x < subCw4; x += ut.W)
                            {
                                int tbx = bxAbs + (x << SsX), tby = byAbs + (y << SsY);
                                WriteCoefs(uvtx, bs, 1, levels[(y / ut.H) * nctw + x / ut.W], ac, cbx4 + x, lc, cby4 + y,
                                    Math.Max(0, Math.Min(ut.W, (Bw - tbx + SsX) >> SsX)), Math.Max(0, Math.Min(ut.H, (Bh - tby + SsY) >> SsY)), mode);
                            }
                    }
                }
            }

            // UpdateIntraBlockContext.
            for (int i = 0; i < bw4 && bx4 + i < 32; i++)
            {
                above.TxIntra[bx4 + i] = (sbyte)tDim.Lw; above.Tx[bx4 + i] = (sbyte)tDim.Lw; above.Mode[bx4 + i] = (byte)mode;
                above.PalSz[bx4 + i] = 0; above.SegPred[bx4 + i] = 0; above.SkipMode[bx4 + i] = 0; above.Intra[bx4 + i] = 1;
                above.Skip[bx4 + i] = leaf.Skip ? (byte)1 : (byte)0; above.CompType[bx4 + i] = 0; above.Ref0[bx4 + i] = -1; above.Ref1[bx4 + i] = -1;
                above.Filter0[bx4 + i] = Av1Tables.NSwitchableFilters; above.Filter1[bx4 + i] = Av1Tables.NSwitchableFilters;
            }
            for (int j = 0; j < bh4 && by4 + j < 32; j++)
            {
                left.TxIntra[by4 + j] = (sbyte)tDim.Lh; left.Tx[by4 + j] = (sbyte)tDim.Lh; left.Mode[by4 + j] = (byte)mode;
                left.PalSz[by4 + j] = 0; left.SegPred[by4 + j] = 0; left.SkipMode[by4 + j] = 0; left.Intra[by4 + j] = 1;
                left.Skip[by4 + j] = leaf.Skip ? (byte)1 : (byte)0; left.CompType[by4 + j] = 0; left.Ref0[by4 + j] = -1; left.Ref1[by4 + j] = -1;
                left.Filter0[by4 + j] = Av1Tables.NSwitchableFilters; left.Filter1[by4 + j] = Av1Tables.NSwitchableFilters;
            }
            if (hasChroma)
            {
                for (int i = 0; i < cbw4 && cbx4 + i < 32; i++) above.UvMode[cbx4 + i] = (byte)Av1IntraPredMode.Dc;
                for (int j = 0; j < cbh4 && cby4 + j < 32; j++) left.UvMode[cby4 + j] = (byte)Av1IntraPredMode.Dc;
            }
            var tmpl = TemplateFor(leaf, 0);
            Av1RefMvs.SplatMv(rt.R, (byAbs & 31) + 5, in tmpl, bxAbs, bw4, bh4);
        }

        // dav1d read_tx_tree, encoder side: the split flag (below depth 2, above 4x4), the recursion into sub-transforms
        // that start inside the frame, and the leaf's log2 size into the above / left tx contexts.
        private void WriteTxTree(Leaf leaf, int tx, int depth, int xOff, int yOff, int bx, int by)
        {
            int bx4 = bx & 31, by4 = by & 31;
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int txw = tDim.Lw, txh = tDim.Lh;
            bool isSplit = false;
            if (depth < 2 && tx > (int)Av1TxSize.Tx4x4)
            {
                int cat = 2 * ((int)Av1TxSize.Tx64x64 - tDim.Max) - depth;
                int a = above.Tx[bx4] < txw ? 1 : 0, l = left.Tx[by4] < txh ? 1 : 0;
                isSplit = ((depth == 0 ? leaf.Split0 : leaf.Split1) >> (yOff * 4 + xOff) & 1) != 0;
                msac.EncodeBoolAdapt(cdf.GetTxPartCdf(cat, a + l), isSplit ? 1u : 0u);
            }
            if (isSplit && tDim.Max > (int)Av1TxSize.Tx8x8)
            {
                int sub = tDim.Sub;
                ref readonly var sd = ref Av1Tables.TxfmDimensions[sub];
                WriteTxTree(leaf, sub, depth + 1, xOff * 2, yOff * 2, bx, by);
                if (txw >= txh && bx + sd.W < Bw) WriteTxTree(leaf, sub, depth + 1, xOff * 2 + 1, yOff * 2, bx + sd.W, by);
                if (txh >= txw && by + sd.H < Bh)
                {
                    WriteTxTree(leaf, sub, depth + 1, xOff * 2, yOff * 2 + 1, bx, by + sd.H);
                    if (txw >= txh && bx + sd.W < Bw) WriteTxTree(leaf, sub, depth + 1, xOff * 2 + 1, yOff * 2 + 1, bx + sd.W, by + sd.H);
                }
            }
            else
            {
                Av1BlockContextManaged.Fill(above.Tx, bx4, tDim.W, (sbyte)(isSplit ? 0 : txw));
                Av1BlockContextManaged.Fill(left.Tx, by4, tDim.H, (sbyte)(isSplit ? 0 : txh));
            }
        }

        // dav1d read_coef_tree, encoder side: the coefficients of each transform leaf in tree order.
        private void WriteCoefTree(Leaf leaf, int bs, int tx, int depth, int xOff, int yOff, int bx, int by)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int txw = tDim.W, txh = tDim.H;
            int mask = depth == 0 ? leaf.Split0 : depth == 1 ? leaf.Split1 : 0;
            if (depth < 2 && (mask >> (yOff * 4 + xOff) & 1) != 0)
            {
                int sub = tDim.Sub;
                ref readonly var sd = ref Av1Tables.TxfmDimensions[sub];
                WriteCoefTree(leaf, bs, sub, depth + 1, xOff * 2, yOff * 2, bx, by);
                if (txw >= txh && bx + sd.W < Bw) WriteCoefTree(leaf, bs, sub, depth + 1, xOff * 2 + 1, yOff * 2, bx + sd.W, by);
                if (txh >= txw && by + sd.H < Bh)
                {
                    WriteCoefTree(leaf, bs, sub, depth + 1, xOff * 2, yOff * 2 + 1, bx, by + sd.H);
                    if (txw >= txh && bx + sd.W < Bw) WriteCoefTree(leaf, bs, sub, depth + 1, xOff * 2 + 1, yOff * 2 + 1, bx + sd.W, by + sd.H);
                }
                return;
            }
            WriteCoefs(tx, bs, 0, leaf.LumaTx[(bx, by)], above.LCoef, bx & 31, left.LCoef, by & 31,
                Math.Min(txw, Bw - bx), Math.Min(txh, Bh - by));
        }

        private static void FillCoefCtx(byte[] arr, int off, int n, byte v)
        {
            int cnt = Math.Min(n, arr.Length - off);
            if (cnt > 0) Array.Fill(arr, v, off, cnt);
        }

        private void WriteCoefs(int tx, int bs, int chroma, int[] levels, byte[] a, int aOff, byte[] l, int lOff, int ctw, int cth,
            int intraYMode = -1)
        {
            ref readonly var tDim = ref Av1Tables.TxfmDimensions[tx];
            int aLen = Math.Max(1, Math.Min(tDim.W, 32 - aOff)), lLen = Math.Max(1, Math.Min(tDim.H, 32 - lOff));
            int skipCtx = Av1CoeffDecode.GetSkipCtx(in tDim, bs, a.AsSpan(aOff, aLen), l.AsSpan(lOff, lLen), chroma, (int)Layout);
            int signCtx = Av1CoeffDecode.GetDcSignCtx(tx, a.AsSpan(aOff, aLen), l.AsSpan(lOff, lLen));
            Av1CoeffEncode.EncodeCoefs(msac, cdf.Coef, cdf.Mode, tx, chroma, Math.Max(intraYMode, 0), levels, skipCtx, signCtx, 1,
                lossless: Lossless, inter: intraYMode < 0);
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
