// DCT-aware entropy coder for JPEG-recompression coefficients. Reuses the lossy VarDCT AC context model
// (non-zero-count prediction from neighbouring blocks, per-frequency-band coefficient contexts) and the ANS
// entropy machinery to code a JPEG's quantized coefficients far tighter than JPEG's per-block Huffman. DC is
// clamped-gradient predicted; AC is walked in zigzag (frequency) order. Both directions are implemented here
// and controlled entirely by this repo, so byte-exact reconstruction is guaranteed by JpegCoder.RebuildJpeg.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal static partial class JxlEncoder
{
    private const int JpegBlockClusters = 3;                       // one AC block-context per component
    private const int JpegAcContexts = 495 * JpegBlockClusters;    // 1485, matches the VarDCT context layout
    private const int JpegDcContextBase = JpegAcContexts;          // DC contexts follow the AC ones
    private const int JpegTotalContexts = JpegDcContextBase + JpegBlockClusters;

    private static int ClampGradient(int left, int above, int aboveLeft)
    {
        int g = left + above - aboveLeft;
        int lo = Math.Min(left, above), hi = Math.Max(left, above);
        return Math.Clamp(g, lo, hi);
    }

    // Per-component AC scan order: position 0 is DC; positions 1..63 are the natural-order coefficient
    // indices sorted by descending nonzero frequency across the component's blocks. Visiting the most-often-
    // nonzero positions first makes the running-nonzeros count fall predictably, tightening the coeff model.
    private static int[][] ComputeOrders(Formats.JpegDctData d)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var orders = new int[d.ComponentCount][];
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            var nzCount = new long[64];
            foreach (int[] block in comp.Blocks)
            {
                for (int oi = 1; oi < 64; oi++)
                {
                    if (block[zz[oi]] != 0)
                    {
                        nzCount[oi]++;
                    }
                }
            }

            // Stable descending sort of AC scan positions 1..63 by nonzero count (ties keep zigzag order).
            var slots = new int[63];
            for (int i = 0; i < 63; i++)
            {
                slots[i] = i + 1;
            }

            Array.Sort(slots, (a, b) => nzCount[a] != nzCount[b] ? nzCount[b].CompareTo(nzCount[a]) : a.CompareTo(b));

            var order = new int[64];
            order[0] = 0; // DC
            for (int i = 0; i < 63; i++)
            {
                order[i + 1] = zz[slots[i]]; // natural-order index visited at scan step i+1
            }

            orders[ci] = order;
        }

        return orders;
    }

    // Builds the token + per-token-context stream for one baseline JPEG's coefficients (all components).
    private static void BuildJpegTokens(Formats.JpegDctData d, int[][] orders, List<ModToken> toks, List<int> ctxs)
    {
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            int blockCtx = ci;
            var nonZerosGrid = new uint[bpr];
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    // DC: clamped-gradient prediction over the block grid (the DC plane is smooth).
                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    ctxs.Add(JpegDcContextBase + ci);
                    toks.Add(HybridToken(PackSigned(block[0] - ClampGradient(left, above, aboveLeft))));

                    // AC: non-zero count (predicted from neighbours), then coefficients in zigzag order.
                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * JpegBlockClusters);

                    int nonZeros = 0, lastNz = 0;
                    for (int oi = 1; oi < 64; oi++)
                    {
                        if (block[order[oi]] != 0)
                        {
                            nonZeros++;
                            lastNz = oi;
                        }
                    }

                    ctxs.Add(nonZerosCtx);
                    toks.Add(HybridToken(nonZeros));
                    nonZerosGrid[bx] = (uint)nonZeros;
                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * JpegBlockClusters);
                    int rem = nonZeros;
                    for (int oi = 1; oi <= lastNz; oi++)
                    {
                        int fidx = oi - 1;
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[rem - 1] + CoeffFreqContext[fidx]) * 2) + isPrevNonzero);
                        ctxs.Add(coeffCtxBase + coeffCtx);
                        int q = block[order[oi]];
                        if (q == 0)
                        {
                            toks.Add(HybridToken(0));
                            isPrevNonzero = 0;
                            continue;
                        }

                        toks.Add(HybridToken(PackSigned(q)));
                        isPrevNonzero = 1;
                        if (--rem == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Entropy-codes a JPEG's quantized DCT coefficients with the DCT-aware context model + ANS.</summary>
    internal static byte[] EncodeJpegCoefficients(Formats.JpegDctData d)
    {
        // Optimized per-component scan order tightens the coefficient model but costs a transmitted order
        // table; on tiny images that table can outweigh the saving, so encode both ways and keep the smaller.
        byte[] custom = EncodeWithOrders(d, ComputeOrders(d), useCustomOrder: true);
        byte[] natural = EncodeWithOrders(d, NaturalOrders(d.ComponentCount), useCustomOrder: false);
        return custom.Length <= natural.Length ? custom : natural;
    }

    private static int[][] NaturalOrders(int componentCount)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var orders = new int[componentCount][];
        for (int ci = 0; ci < componentCount; ci++)
        {
            var order = new int[64];
            for (int oi = 0; oi < 64; oi++)
            {
                order[oi] = zz[oi];
            }

            orders[ci] = order;
        }

        return orders;
    }

    private static byte[] EncodeWithOrders(Formats.JpegDctData d, int[][] orders, bool useCustomOrder)
    {
        var toks = new List<ModToken>();
        var ctxs = new List<int>();
        BuildJpegTokens(d, orders, toks, ctxs);

        int maxTok = 0;
        foreach (ModToken t in toks)
        {
            if (t.Sym > maxTok)
            {
                maxTok = t.Sym;
            }
        }

        int alphabet = maxTok + 1;
        int logAlpha = Math.Max(5, BitLen(maxTok));
        var ctxHist = new long[JpegTotalContexts][];
        for (int i = 0; i < JpegTotalContexts; i++)
        {
            ctxHist[i] = new long[alphabet];
        }

        for (int i = 0; i < toks.Count; i++)
        {
            ctxHist[ctxs[i]][toks[i].Sym]++;
        }

        (int[] map, int[][] norm, int k) = ClusterContextsTotalCost(ctxHist, alphabet, MaxHfClusters, logAlpha);

        var w = new JxlBitWriter();
        w.WriteBits((uint)toks.Count, 32);
        w.WriteBool(useCustomOrder);
        if (useCustomOrder)
        {
            // Per-component AC scan orders (63 natural-order indices x 6 bits), needed to reverse the reorder.
            for (int ci = 0; ci < d.ComponentCount; ci++)
            {
                for (int oi = 1; oi < 64; oi++)
                {
                    w.WriteBits((uint)orders[ci][oi], 6);
                }
            }
        }

        // Entropy header, byte-for-byte the layout DecodeHistograms consumes: lz77, context map,
        // use_prefix, logAlpha, per-cluster uint configs, per-cluster histograms.
        w.WriteBool(false); // lz77 disabled
        WriteContextMap(w, (int[])map.Clone(), k);
        w.WriteBool(false); // use_prefix_code = false (ANS)
        w.WriteBits((uint)(logAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(w, LitSplit, LitMsb, LitLsb, logAlpha);
        }

        for (int c = 0; c < k; c++)
        {
            JxlEntropy.WriteHistogram(w, norm[c], JxlEntropy.HistShift);
        }

        var ans = new JxlAnsWriter(norm, logAlpha);
        var ansToks = new List<AnsToken>(toks.Count);
        for (int i = 0; i < toks.Count; i++)
        {
            ModToken t = toks[i];
            ansToks.Add(new AnsToken(map[ctxs[i]], t.Sym, t.Bits, t.N));
        }

        ans.Encode(w, ansToks);
        return w.ToArray();
    }

    /// <summary>Decodes the coefficients back into d.Components[*].Blocks (BlocksPerRow/Col must be set).</summary>
    internal static void DecodeJpegCoefficients(byte[] data, Formats.JpegDctData d)
    {
        byte[] zz = Compression.JpegTables.NaturalOrder;
        var br = new JxlBitReader(data);
        int tokenCount = (int)br.ReadBits(32);
        _ = tokenCount; // blocks are walked by structure; the count is a stored sanity value only
        bool useCustomOrder = br.ReadBool();
        var orders = new int[d.ComponentCount][];
        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            var order = new int[64];
            order[0] = 0;
            for (int oi = 1; oi < 64; oi++)
            {
                order[oi] = useCustomOrder ? (int)br.ReadBits(6) : zz[oi];
            }

            orders[ci] = order;
        }

        // DecodeHistograms reads the whole entropy header (lz77, context map, use_prefix, logAlpha, configs, histograms).
        JxlAnsCode code = JxlEntropy.DecodeHistograms(JpegTotalContexts, br);
        var rd = new JxlAnsReader(code, br);

        for (int ci = 0; ci < d.ComponentCount; ci++)
        {
            Formats.JpegDctComponent comp = d.Components[ci];
            int[] order = orders[ci];
            int bpr = comp.BlocksPerRow, bpc = comp.BlocksPerCol;
            int blockCtx = ci;
            comp.Blocks = new int[bpr * bpc][];
            for (int i = 0; i < comp.Blocks.Length; i++)
            {
                comp.Blocks[i] = new int[64];
            }

            var nonZerosGrid = new uint[bpr];
            for (int by = 0; by < bpc; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    int[] block = comp.Blocks[(by * bpr) + bx];

                    int left = bx > 0 ? comp.Blocks[(by * bpr) + bx - 1][0] : (by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : 0);
                    int above = by > 0 ? comp.Blocks[((by - 1) * bpr) + bx][0] : left;
                    int aboveLeft = bx > 0 && by > 0 ? comp.Blocks[((by - 1) * bpr) + bx - 1][0] : above;
                    int dcRes = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(JpegDcContextBase + ci));
                    block[0] = dcRes + ClampGradient(left, above, aboveLeft);

                    uint predicted = by == 0
                        ? (bx == 0 ? 32u : nonZerosGrid[bx - 1])
                        : (bx == 0 ? nonZerosGrid[bx] : (nonZerosGrid[bx] + nonZerosGrid[bx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * JpegBlockClusters);
                    int nonZeros = (int)rd.ReadHybridUintCtx(nonZerosCtx);
                    nonZerosGrid[bx] = (uint)nonZeros;
                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * JpegBlockClusters);
                    int rem = nonZeros;
                    for (int oi = 1; oi < 64; oi++)
                    {
                        int fidx = oi - 1;
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[rem - 1] + CoeffFreqContext[fidx]) * 2) + isPrevNonzero);
                        int val = JxlBits.UnpackSigned(rd.ReadHybridUintCtx(coeffCtxBase + coeffCtx));
                        if (val == 0)
                        {
                            isPrevNonzero = 0;
                            continue;
                        }

                        block[order[oi]] = val;
                        isPrevNonzero = 1;
                        if (--rem == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }
}
