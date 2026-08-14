// HEVC I-slice encoder core: encodes a YUV 4:2:0 frame (dimensions padded to a multiple of the CTU
// size) as an all-intra HEVC slice, reconstructing each block as it goes so neighbour references
// match the decoder exactly. Structure: one 32x32 intra CU per 32x32 CTU (part 2Nx2N) with a SATD
// luma mode search over all 35 modes, and a rate-distortion-decided transform quadtree (32->16->8
// luma TUs) with per-TU intra prediction. Mirrors HevcDecoder's coding_unit / transform_tree /
// transform_unit syntax; residual via HevcResidualEncoder, availability via HevcZScan.
using System;

namespace SharpImage.Formats.Hevc;

internal sealed class HevcIntraFrameEncoder
{
    private const int CtbLog2 = 5;
    private const int CtbSize = 32;

    private readonly int pw;   // padded luma width  (multiple of 32)
    private readonly int ph;   // padded luma height
    private readonly int cw;   // padded chroma width  = pw/2
    private readonly int ch;   // padded chroma height = ph/2
    private readonly int qp;
    private readonly bool signDataHiding;

    private readonly byte[] lumaOrig, cbOrig, crOrig;
    private readonly byte[] lumaRec, cbRec, crRec;
    private readonly byte[] tabIpm; // intra luma mode per 4x4 (for MPM), width pw/4

    private static readonly int[] LevelScale = { 40, 45, 51, 57, 64, 72 };

    // Chroma QP table (HEVC Table 8-6, 4:2:0).
    private static readonly byte[] ChromaQp =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
        26, 27, 28, 29, 29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 39, 40, 41, 42,
        43, 44, 45, 46, 47, 48, 49, 50, 51,
    };

    public HevcIntraFrameEncoder(byte[] luma, byte[] cb, byte[] cr, int paddedW, int paddedH, int qp, bool signDataHiding)
    {
        pw = paddedW;
        ph = paddedH;
        cw = paddedW / 2;
        ch = paddedH / 2;
        this.qp = qp;
        this.signDataHiding = signDataHiding;
        lumaOrig = luma;
        cbOrig = cb;
        crOrig = cr;
        lumaRec = new byte[pw * ph];
        cbRec = new byte[cw * ch];
        crRec = new byte[cw * ch];
        tabIpm = new byte[(pw / 4) * (ph / 4)];
    }

    public int ChromaQpFor(int lumaQp) => ChromaQp[Math.Clamp(lumaQp, 0, 57)];

    // Encoder's own reconstruction (for verifying enc/dec consistency).
    internal byte[] LumaRecon => lumaRec;

    internal byte[] CbRecon => cbRec;

    internal byte[] CrRecon => crRec;

    internal int PaddedWidth => pw;

    /// <summary>Encodes the slice segment data (RBSP payload after the slice header), byte-aligned.</summary>
    public byte[] EncodeSliceData()
    {
        var writer = new HevcBitWriter();
        var cabac = new HevcCabacEncoder(writer);
        cabac.InitializeContexts(qp, 0); // I-slice
        cabac.Start();

        int ctbsX = pw / CtbSize;
        int ctbsY = ph / CtbSize;
        int total = ctbsX * ctbsY;
        int idx = 0;
        for (int cy = 0; cy < ctbsY; cy++)
        {
            for (int cx = 0; cx < ctbsX; cx++)
            {
                EncodeCtu(cabac, cx * CtbSize, cy * CtbSize);
                idx++;
                cabac.EncodeTerminate(idx == total ? 1 : 0); // end_of_slice_segment_flag
            }
        }

        cabac.Finish();
        if (!writer.IsByteAligned)
        {
            // rbsp trailing after cabac flush
            writer.ByteAlignWithStopBit();
        }

        return writer.ToArray();
    }

    private void EncodeCtu(HevcCabacEncoder cabac, int x0, int y0)
    {
        // coding_quadtree: MinCbLog2 == CtbLog2 == 5, so no split_cu_flag; one CU per CTU.
        // coding_unit (I-slice, intra): part_mode (2Nx2N), intra modes, transform_tree.
        // part_mode: log2CbSize == MinCbLog2SizeY so it is coded; 2Nx2N -> bin 1.
        cabac.EncodeBin(HevcCabacContextIndex.PartMode, 1);

        // --- Luma intra mode selection (SATD over all 35 modes) ---
        int lumaMode = SelectLumaMode(x0, y0);

        // MPM candidates + signalling.
        Span<int> cand = stackalloc int[3];
        DeriveMpm(x0, y0, cand);
        EncodeLumaMode(cabac, lumaMode, cand);

        // Store luma mode into tabIpm for the 32x32 CU region.
        StoreIpm(x0, y0, CtbSize, lumaMode);

        // Chroma mode: derived from luma (signal 4).
        cabac.EncodeBin(HevcCabacContextIndex.IntraChromaPredMode, 0);

        // Plan the transform quadtree (per-TU prediction + reconstruction, RD-decided split),
        // then encode it. Chroma is carried at every leaf (luma TU >= 8 so chroma TU >= 4).
        double lambda = 0.85 * Math.Pow(2.0, (qp - 12) / 3.0);
        TuNode root = PlanTu(x0, y0, CtbLog2, 0, lumaMode, lambda);
        EncodeTuTree(cabac, root, 0, true, true, lumaMode);
    }

    // ---- Transform quadtree ----

    private sealed class TuNode
    {
        public bool Split;
        public TuNode[]? Children;
        public int X, Y, Log2Size;
        public short[] LumaCoeff = System.Array.Empty<short>();
        public bool CbfLuma;
        public short[] CbCoeff = System.Array.Empty<short>();
        public short[] CrCoeff = System.Array.Empty<short>();
        public bool CbfCb, CbfCr;
    }

    // Decides the TU structure by rate-distortion, reconstructing luma+chroma into the recon buffers
    // as it commits, and returns the plan + its cost.
    private TuNode PlanTu(int x, int y, int log2Size, int depth, int lumaMode, double lambda)
    {
        bool canSplit = log2Size > 3 && depth < 2; // 32->16->8 (min luma TU 8, chroma 4)

        // Leaf: process this TU (reconstruct), remember its recon + cost.
        byte[] savePre = SnapshotRegion(x, y, 1 << log2Size);
        var leaf = ProcessTuLeaf(x, y, log2Size, lumaMode);
        double leafCost = leaf.Dist + (lambda * leaf.Bits);
        if (!canSplit)
        {
            return leaf.Node;
        }

        byte[] leafRecon = SnapshotRegion(x, y, 1 << log2Size);

        // Split: restore, then reconstruct 4 children in z-scan order.
        RestoreRegion(x, y, 1 << log2Size, savePre);
        int half = 1 << (log2Size - 1);
        var c0 = PlanTu(x, y, log2Size - 1, depth + 1, lumaMode, lambda);
        var c1 = PlanTu(x + half, y, log2Size - 1, depth + 1, lumaMode, lambda);
        var c2 = PlanTu(x, y + half, log2Size - 1, depth + 1, lumaMode, lambda);
        var c3 = PlanTu(x + half, y + half, log2Size - 1, depth + 1, lumaMode, lambda);
        double splitCost = (lambda * 1) + RegionRdCost(x, y, log2Size, lambda, c0, c1, c2, c3);

        if (leafCost <= splitCost)
        {
            RestoreRegion(x, y, 1 << log2Size, leafRecon);
            return leaf.Node;
        }

        return new TuNode { Split = true, Children = new[] { c0, c1, c2, c3 }, X = x, Y = y, Log2Size = log2Size };
    }

    // Distortion+bits of a chosen split subtree (children already reconstructed in place).
    private double RegionRdCost(int x, int y, int log2Size, double lambda, params TuNode[] children)
    {
        int n = 1 << log2Size;
        long dist = SsdLuma(x, y, n) + SsdChroma(x / 2, y / 2, n / 2);
        double bits = 0;
        foreach (TuNode c in children)
        {
            bits += SubtreeBits(c);
        }

        return dist + (lambda * bits);
    }

    private double SubtreeBits(TuNode node)
    {
        if (node.Split)
        {
            double b = 1;
            foreach (TuNode c in node.Children!)
            {
                b += SubtreeBits(c);
            }

            return b;
        }

        return 3 + EstBits(node.LumaCoeff) + EstBits(node.CbCoeff) + EstBits(node.CrCoeff);
    }

    private (TuNode Node, long Dist, double Bits) ProcessTuLeaf(int x, int y, int log2Size, int lumaMode)
    {
        int n = 1 << log2Size;
        var node = new TuNode { X = x, Y = y, Log2Size = log2Size };

        // Luma
        var lumaCoeff = new short[n * n];
        node.CbfLuma = ProcessPlane(lumaOrig, lumaRec, pw, ph, x, y, n, lumaMode, 0, qp, lumaCoeff);
        node.LumaCoeff = lumaCoeff;

        // Chroma (n/2)
        int cSize = n / 2;
        int cx = x / 2, cy = y / 2;
        int cqp = ChromaQpFor(qp);
        var cbCoeff = new short[cSize * cSize];
        var crCoeff = new short[cSize * cSize];
        node.CbfCb = ProcessPlane(cbOrig, cbRec, cw, ch, cx, cy, cSize, lumaMode, 1, cqp, cbCoeff);
        node.CbfCr = ProcessPlane(crOrig, crRec, cw, ch, cx, cy, cSize, lumaMode, 2, cqp, crCoeff);
        node.CbCoeff = cbCoeff;
        node.CrCoeff = crCoeff;

        long dist = SsdLuma(x, y, n) + SsdChroma(cx, cy, cSize);
        double bits = 3 + EstBits(lumaCoeff) + EstBits(cbCoeff) + EstBits(crCoeff);
        return (node, dist, bits);
    }

    private void EncodeTuTree(HevcCabacEncoder cabac, TuNode node, int depth, bool parentCbfCb, bool parentCbfCr, int lumaMode)
    {
        // split_transform_flag is coded when log2Size in {4,5} and depth<2 (matches SPS max intra depth 2).
        bool canSplit = node.Log2Size > 3 && depth < 2;
        if (canSplit)
        {
            cabac.EncodeBin(HevcCabacContextIndex.SplitTransformFlag + 5 - node.Log2Size, node.Split ? 1 : 0);
        }

        // cbf_cb / cbf_cr: coded only when trafoDepth==0 or the parent had the flag set.
        bool cbfCb = ChromaCbf(node, 1);
        bool cbfCr = ChromaCbf(node, 2);
        if (depth == 0 || parentCbfCb)
        {
            cabac.EncodeBin(HevcCabacContextIndex.CbfChroma + depth, cbfCb ? 1 : 0);
        }

        if (depth == 0 || parentCbfCr)
        {
            cabac.EncodeBin(HevcCabacContextIndex.CbfChroma + depth, cbfCr ? 1 : 0);
        }

        if (node.Split)
        {
            foreach (TuNode c in node.Children!)
            {
                EncodeTuTree(cabac, c, depth + 1, cbfCb, cbfCr, lumaMode);
            }

            return;
        }

        // Leaf transform_unit: cbf_luma, then luma + chroma residuals.
        int lumaN = 1 << node.Log2Size;
        cabac.EncodeBin(HevcCabacContextIndex.CbfLuma + (depth == 0 ? 1 : 0), node.CbfLuma ? 1 : 0);
        if (node.CbfLuma)
        {
            HevcResidualEncoder.Encode(cabac, node.LumaCoeff, node.Log2Size, DeriveScan(lumaMode, lumaN), 0, signDataHiding);
        }

        // Chroma scan index uses the LUMA transform size (see DeriveIntraScanIdx call in the decoder).
        int chromaScan = DeriveScan(lumaMode, lumaN);
        if (node.CbfCb)
        {
            HevcResidualEncoder.Encode(cabac, node.CbCoeff, node.Log2Size - 1, chromaScan, 1, signDataHiding);
        }

        if (node.CbfCr)
        {
            HevcResidualEncoder.Encode(cabac, node.CrCoeff, node.Log2Size - 1, chromaScan, 1, signDataHiding);
        }
    }

    // Whether a chroma component has any coded block in this subtree (cbf_cb/cr are signalled at the
    // level where they first become non-zero; here we signal once per level as the OR over the subtree).
    private static bool ChromaCbf(TuNode node, int comp)
    {
        if (!node.Split)
        {
            return comp == 1 ? node.CbfCb : node.CbfCr;
        }

        foreach (TuNode c in node.Children!)
        {
            if (ChromaCbf(c, comp))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Mode selection ----

    private int SelectLumaMode(int x0, int y0)
    {
        int best = 0;
        long bestCost = long.MaxValue;
        var avail = HevcZScan.LumaAvail(x0, y0, CtbSize, pw, ph);
        Span<int> pred = stackalloc int[CtbSize * CtbSize];
        for (int mode = 0; mode < 35; mode++)
        {
            HevcIntraPrediction.Predict(lumaRec, pw, x0, y0, CtbSize, 8, 0, mode, avail, pred, false, true);
            long cost = Satd(lumaOrig, pw, x0, y0, CtbSize, pred);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = mode;
            }
        }

        return best;
    }

    private long Satd(byte[] orig, int stride, int x0, int y0, int n, ReadOnlySpan<int> pred)
    {
        // Sum of absolute differences (fast proxy; SATD-quality search is a later refinement).
        long sad = 0;
        for (int y = 0; y < n; y++)
        {
            int row = (y0 + y) * stride;
            for (int x = 0; x < n; x++)
            {
                sad += Math.Abs(orig[row + x0 + x] - pred[(y * n) + x]);
            }
        }

        return sad;
    }

    // ---- Prediction + transform + reconstruction (per transform unit) ----

    // Predicts, transforms, quantizes (+ sign-hiding), and reconstructs one plane's TU into `rec`.
    // Returns whether the block has any nonzero coefficient (cbf). Chroma mode == luma mode.

    private bool ProcessPlane(byte[] orig, byte[] rec, int stride, int planeH, int x, int y, int n,
        int mode, int cIdx, int blockQp, short[] coeff)
    {
        var avail = cIdx == 0 ? HevcZScan.LumaAvail(x, y, n, pw, ph) : HevcZScan.ChromaAvail(x, y, n, cw, ch);
        var pred = new int[n * n];
        HevcIntraPrediction.Predict(rec, stride, x, y, n, 8, cIdx, mode, avail, pred, false, true);

        var resid = new short[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                resid[(j * n) + i] = (short)(orig[((y + j) * stride) + x + i] - pred[(j * n) + i]);
            }
        }

        bool useDst = cIdx == 0 && n == 4;
        HevcForwardTransform.Forward(resid, coeff, n, 8, useDst);
        // The intra scan index is derived from the LUMA transform size for both planes (HEVC 7.4.9.11 /
        // the decoder passes log2TrafoSize, not log2TrafoSizeC, to DeriveIntraScanIdx).
        int scan = DeriveScan(mode, cIdx == 0 ? n : n * 2);
        var orig2 = (short[])coeff.Clone();
        var deltaU = new int[n * n];
        HevcForwardTransform.Quantize(coeff, blockQp, n, 8, true, deltaU);
        if (signDataHiding)
        {
            HevcForwardTransform.ApplySignHiding(coeff, orig2, deltaU, n, scan);
        }

        bool cbf = false;
        for (int i = 0; i < n * n; i++)
        {
            if (coeff[i] != 0) { cbf = true; break; }
        }

        var recResid = new short[n * n];
        if (cbf)
        {
            var deq = (short[])coeff.Clone();
            Dequantize(deq, n, blockQp);
            HevcTransform.InverseTransform(deq, recResid, n, useDst, 8);
        }

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = pred[(j * n) + i] + recResid[(j * n) + i];
                rec[((y + j) * stride) + x + i] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return cbf;
    }

    // Intra scan index (0=diag, 1=horiz, 2=vert): mode-dependent only for 4x4/8x8.
    private static int DeriveScan(int mode, int n)
    {
        if (n == 4 || n == 8)
        {
            if (mode >= 6 && mode <= 14)
            {
                return 2;
            }

            if (mode >= 22 && mode <= 30)
            {
                return 1;
            }
        }

        return 0;
    }

    private long SsdLuma(int x, int y, int n)
    {
        long s = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int d = lumaOrig[((y + j) * pw) + x + i] - lumaRec[((y + j) * pw) + x + i];
                s += (long)d * d;
            }
        }

        return s;
    }

    private long SsdChroma(int cx, int cy, int n)
    {
        long s = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int d1 = cbOrig[((cy + j) * cw) + cx + i] - cbRec[((cy + j) * cw) + cx + i];
                int d2 = crOrig[((cy + j) * cw) + cx + i] - crRec[((cy + j) * cw) + cx + i];
                s += ((long)d1 * d1) + ((long)d2 * d2);
            }
        }

        return s;
    }

    private static double EstBits(short[] coeff)
    {
        double b = 0;
        foreach (short c in coeff)
        {
            if (c != 0)
            {
                int a = Math.Abs(c);
                b += 2 + (2 * (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)a)));
            }
        }

        return b;
    }

    private byte[] SnapshotRegion(int x, int y, int n)
    {
        int cn = n / 2, cx = x / 2, cy = y / 2;
        var buf = new byte[(n * n) + (2 * cn * cn)];
        int o = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                buf[o++] = lumaRec[((y + j) * pw) + x + i];
            }
        }

        for (int j = 0; j < cn; j++)
        {
            for (int i = 0; i < cn; i++)
            {
                buf[o++] = cbRec[((cy + j) * cw) + cx + i];
            }
        }

        for (int j = 0; j < cn; j++)
        {
            for (int i = 0; i < cn; i++)
            {
                buf[o++] = crRec[((cy + j) * cw) + cx + i];
            }
        }

        return buf;
    }

    private void RestoreRegion(int x, int y, int n, byte[] buf)
    {
        int cn = n / 2, cx = x / 2, cy = y / 2;
        int o = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                lumaRec[((y + j) * pw) + x + i] = buf[o++];
            }
        }

        for (int j = 0; j < cn; j++)
        {
            for (int i = 0; i < cn; i++)
            {
                cbRec[((cy + j) * cw) + cx + i] = buf[o++];
            }
        }

        for (int j = 0; j < cn; j++)
        {
            for (int i = 0; i < cn; i++)
            {
                crRec[((cy + j) * cw) + cx + i] = buf[o++];
            }
        }
    }

    private void Dequantize(Span<short> coeff, int n, int blockQp)
    {
        int log2 = n switch { 4 => 2, 8 => 3, 16 => 4, _ => 5 };
        int qpPrime = blockQp; // 8-bit
        int shift = 8 + log2 - 5;
        int add = shift > 0 ? 1 << (shift - 1) : 0;
        long scale = (long)LevelScale[qpPrime % 6] << (qpPrime / 6);
        for (int i = 0; i < n * n; i++)
        {
            if (coeff[i] == 0)
            {
                continue;
            }

            long d = ((coeff[i] * scale * 16) + add) >> shift;
            coeff[i] = (short)Math.Clamp(d, -32768, 32767);
        }
    }

    // ---- Intra mode signalling ----

    private void DeriveMpm(int x0, int y0, Span<int> cand)
    {
        int minPuW = pw / 4;
        int xPu = x0 >> 2, yPu = y0 >> 2;
        int candUp = y0 > 0 ? tabIpm[((yPu - 1) * minPuW) + xPu] : 1;
        int candLeft = x0 > 0 ? tabIpm[(yPu * minPuW) + xPu - 1] : 1;

        // Intra pred mode prediction doesn't cross vertical CTB boundaries (top of CTB row).
        int yCtb = (y0 >> CtbLog2) << CtbLog2;
        if (y0 - 1 < yCtb)
        {
            candUp = 1;
        }

        if (candLeft == candUp)
        {
            if (candLeft < 2)
            {
                cand[0] = 0;
                cand[1] = 1;
                cand[2] = 26;
            }
            else
            {
                cand[0] = candLeft;
                cand[1] = 2 + ((candLeft - 2 - 1 + 32) & 31);
                cand[2] = 2 + ((candLeft - 2 + 1) & 31);
            }
        }
        else
        {
            cand[0] = candLeft;
            cand[1] = candUp;
            if (cand[0] != 0 && cand[1] != 0)
            {
                cand[2] = 0;
            }
            else if (cand[0] != 1 && cand[1] != 1)
            {
                cand[2] = 1;
            }
            else
            {
                cand[2] = 26;
            }
        }
    }

    private void EncodeLumaMode(HevcCabacEncoder cabac, int mode, ReadOnlySpan<int> cand)
    {
        int mpmIdx = -1;
        for (int i = 0; i < 3; i++)
        {
            if (cand[i] == mode) { mpmIdx = i; break; }
        }

        if (mpmIdx >= 0)
        {
            cabac.EncodeBin(HevcCabacContextIndex.PrevIntraLumaPredFlag, 1);
            // mpm_idx: truncated unary, max 2 (bypass).
            for (int b = 0; b < mpmIdx; b++)
            {
                cabac.EncodeBypass(1);
            }

            if (mpmIdx < 2)
            {
                cabac.EncodeBypass(0);
            }
        }
        else
        {
            cabac.EncodeBin(HevcCabacContextIndex.PrevIntraLumaPredFlag, 0);
            Span<int> sorted = stackalloc int[3] { cand[0], cand[1], cand[2] };
            if (sorted[0] > sorted[1]) { (sorted[0], sorted[1]) = (sorted[1], sorted[0]); }
            if (sorted[0] > sorted[2]) { (sorted[0], sorted[2]) = (sorted[2], sorted[0]); }
            if (sorted[1] > sorted[2]) { (sorted[1], sorted[2]) = (sorted[2], sorted[1]); }
            int rem = mode;
            for (int i = 0; i < 3; i++)
            {
                if (mode > sorted[i])
                {
                    rem--;
                }
            }

            cabac.EncodeBypassBins((uint)rem, 5);
        }
    }

    private void StoreIpm(int x0, int y0, int n, int mode)
    {
        int minPuW = pw / 4;
        int xPu = x0 >> 2, yPu = y0 >> 2, sizePu = n >> 2;
        for (int i = 0; i < sizePu; i++)
        {
            for (int j = 0; j < sizePu; j++)
            {
                int py = yPu + i, px = xPu + j;
                if (py < ph / 4 && px < minPuW)
                {
                    tabIpm[(py * minPuW) + px] = (byte)mode;
                }
            }
        }
    }
}
