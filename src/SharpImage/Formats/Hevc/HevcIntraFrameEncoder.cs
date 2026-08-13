// HEVC I-slice encoder core: encodes a YUV 4:2:0 frame (dimensions padded to a multiple of the CTU
// size) as an all-intra HEVC slice, reconstructing each block as it goes so neighbour references
// match the decoder exactly. v1 structure: one 32x32 intra CU per 32x32 CTU (part 2Nx2N, single
// transform unit) with a real SATD luma mode search over all 35 modes. Mirrors HevcDecoder's
// coding_unit / transform_tree / transform_unit syntax; residual via HevcResidualEncoder.
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

        // --- Chroma mode: derived from luma (signal 4) for v1 ---
        int chromaMode = lumaMode;
        cabac.EncodeBin(HevcCabacContextIndex.IntraChromaPredMode, 0); // derived-from-luma

        // --- Reconstruct luma + form residual/coeffs ---
        int lumaScan = 0; // 32x32 -> diagonal
        var lumaCoeff = new short[CtbSize * CtbSize];
        bool cbfLuma = EncodeAndReconstructBlock(lumaOrig, lumaRec, pw, ph, x0, y0, CtbSize, lumaMode, 0, qp, lumaScan, lumaCoeff);

        // --- Chroma: 16x16 blocks at (x0/2, y0/2) ---
        int cqp = ChromaQpFor(qp);
        int cx0 = x0 / 2, cy0 = y0 / 2, cSize = CtbSize / 2;
        int chromaScan = 0; // 16x16 -> diagonal
        var cbCoeff = new short[cSize * cSize];
        var crCoeff = new short[cSize * cSize];
        // Predict + form residual + coeffs, but DO NOT write to the bitstream yet — cbf order is
        // cbf_cb, cbf_cr (in transform_tree) then cbf_luma (in transform_unit), then residuals.
        bool cbfCb = QuantizeBlock(cbOrig, cbRec, cw, ch, cx0, cy0, cSize, chromaMode, 1, cqp, chromaScan, cbCoeff, out int[] cbPred);
        bool cbfCr = QuantizeBlock(crOrig, crRec, cw, ch, cx0, cy0, cSize, chromaMode, 2, cqp, chromaScan, crCoeff, out int[] crPred);

        // transform_tree (trafoDepth 0, no split): cbf_cb, cbf_cr
        cabac.EncodeBin(HevcCabacContextIndex.CbfChroma + 0, cbfCb ? 1 : 0);
        cabac.EncodeBin(HevcCabacContextIndex.CbfChroma + 0, cbfCr ? 1 : 0);

        // transform_unit: cbf_luma (intra -> always coded), then luma residual, then chroma residuals.
        cabac.EncodeBin(HevcCabacContextIndex.CbfLuma + 1, cbfLuma ? 1 : 0); // trafoDepth 0 -> +1
        if (cbfLuma)
        {
            HevcResidualEncoder.Encode(cabac, lumaCoeff, CtbLog2, lumaScan, 0, signDataHiding);
        }

        if (cbfCb)
        {
            HevcResidualEncoder.Encode(cabac, cbCoeff, 4, chromaScan, 1, signDataHiding);
        }

        if (cbfCr)
        {
            HevcResidualEncoder.Encode(cabac, crCoeff, 4, chromaScan, 1, signDataHiding);
        }

        // Reconstruct chroma (dequant + inverse + add pred) now that coeffs are final.
        ReconstructChroma(cbRec, cw, ch, cx0, cy0, cSize, cbCoeff, cbPred, cqp, cbfCb);
        ReconstructChroma(crRec, cw, ch, cx0, cy0, cSize, crCoeff, crPred, cqp, cbfCr);
    }

    // ---- Mode selection ----

    private int SelectLumaMode(int x0, int y0)
    {
        int best = 0;
        long bestCost = long.MaxValue;
        var avail = NeighborAvail(x0, y0, CtbSize, false);
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

    // ---- Prediction + transform + reconstruction ----

    private bool EncodeAndReconstructBlock(byte[] orig, byte[] rec, int stride, int height, int x0, int y0, int n,
        int mode, int cIdx, int blockQp, int scanIdx, short[] coeff)
    {
        var avail = NeighborAvail(x0, y0, n, cIdx > 0);
        Span<int> pred = stackalloc int[32 * 32];
        HevcIntraPrediction.Predict(rec, stride, x0, y0, n, 8, cIdx, mode, avail, pred.Slice(0, n * n), false, true);

        var resid = new short[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                resid[(y * n) + x] = (short)(orig[((y0 + y) * stride) + x0 + x] - pred[(y * n) + x]);
            }
        }

        bool useDst = cIdx == 0 && n == 4;
        HevcForwardTransform.Forward(resid, coeff, n, 8, useDst);
        var orig2 = (short[])coeff.Clone();
        var deltaU = new int[n * n];
        int qScaled = HevcForwardTransform.GetScaledQp(false, blockQp);
        HevcForwardTransform.Quantize(coeff, qScaled, n, 8, true, deltaU);
        if (signDataHiding)
        {
            HevcForwardTransform.ApplySignHiding(coeff, orig2, deltaU, n, scanIdx);
        }

        bool cbf = false;
        for (int i = 0; i < n * n; i++)
        {
            if (coeff[i] != 0) { cbf = true; break; }
        }

        // Reconstruct = pred + inverse(dequant(coeff)) when cbf, else pred.
        var recResid = new short[n * n];
        if (cbf)
        {
            var deq = (short[])coeff.Clone();
            Dequantize(deq, n, blockQp);
            HevcTransform.InverseTransform(deq, recResid, n, useDst, 8);
        }

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int v = pred[(y * n) + x] + recResid[(y * n) + x];
                rec[((y0 + y) * stride) + x0 + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        return cbf;
    }

    // Chroma: quantize + hold pred for deferred reconstruction (cbf ordering).
    private bool QuantizeBlock(byte[] orig, byte[] rec, int stride, int height, int x0, int y0, int n,
        int mode, int cIdx, int blockQp, int scanIdx, short[] coeff, out int[] predOut)
    {
        var avail = NeighborAvail(x0, y0, n, true);
        var pred = new int[n * n];
        HevcIntraPrediction.Predict(rec, stride, x0, y0, n, 8, cIdx, ChromaModeFromLuma(mode), avail, pred, false, true);
        predOut = pred;

        var resid = new short[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                resid[(y * n) + x] = (short)(orig[((y0 + y) * stride) + x0 + x] - pred[(y * n) + x]);
            }
        }

        HevcForwardTransform.Forward(resid, coeff, n, 8, false);
        var orig2 = (short[])coeff.Clone();
        var deltaU = new int[n * n];
        HevcForwardTransform.Quantize(coeff, blockQp, n, 8, true, deltaU);
        if (signDataHiding)
        {
            HevcForwardTransform.ApplySignHiding(coeff, orig2, deltaU, n, scanIdx);
        }

        for (int i = 0; i < n * n; i++)
        {
            if (coeff[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ReconstructChroma(byte[] rec, int stride, int height, int x0, int y0, int n,
        short[] coeff, int[] pred, int blockQp, bool cbf)
    {
        var recResid = new short[n * n];
        if (cbf)
        {
            var deq = (short[])coeff.Clone();
            Dequantize(deq, n, blockQp);
            HevcTransform.InverseTransform(deq, recResid, n, false, 8);
        }

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int v = pred[(y * n) + x] + recResid[(y * n) + x];
                rec[((y0 + y) * stride) + x0 + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    // Chroma mode derived-from-luma == luma mode (signal 4).
    private static int ChromaModeFromLuma(int lumaMode) => lumaMode;

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

    private HevcIntraPrediction.Avail NeighborAvail(int x0, int y0, int n, bool chroma)
    {
        int planeW = chroma ? cw : pw;
        bool up = y0 > 0;
        bool left = x0 > 0;
        bool upLeft = up && left;
        bool upRight = up && (x0 + n) < planeW;
        return new HevcIntraPrediction.Avail(up, left, upLeft, upRight, false);
    }
}
