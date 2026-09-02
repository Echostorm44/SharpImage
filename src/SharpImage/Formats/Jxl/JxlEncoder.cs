// JPEG XL (lossless Modular) encoder: the inverse of the decoder in this directory. It emits a bare
// codestream (0xFF 0x0A) with an 8-bit sRGB ImageMetadata, a single Modular frame using a one-leaf
// MA tree with the clamped-gradient predictor (predictor 5), and prefix (Huffman) entropy coding
// with the hybrid-uint config (15,0,0) so every packed residual is a literal token. Verified by
// decoding the output with the reference decoders (libjxl / jxl-oxide) — not just round-tripping.
using System;
using System.Collections.Generic;
using SharpImage.Core;
using SharpImage.Image;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

internal static class JxlEncoder
{
    // MA-tree leaf predictor: 6 = self-correcting weighted predictor (see JxlModular.PredictOne).
    private const int WeightedPredictor = 6;

    /// <summary>Group side when a single group covers the image (group_size_shift 3 => 1024).</summary>
    private const int GroupDim = 1024;
    private const int GroupSizeShift = 3; // 128 << 3 == 1024

    /// <summary>Encodes an image as a lossless RGB JPEG XL codestream.</summary>
    public static byte[] EncodeLossless(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("Cannot encode an empty image.");
        }

        // Extract three 8-bit channels (grayscale is expanded to RGB), then decorrelate colour with the
        // reversible YCoCg transform (RCT type 6) so the residuals are far more compressible.
        int nb = 3;
        int[][] chan = new int[nb][];
        for (int c = 0; c < nb; c++)
        {
            chan[c] = new int[w * h];
        }

        int srcCh = image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh;
                int r, g, b;
                if (srcCh == 1)
                {
                    r = g = b = Quantum.ScaleToByte(row[off]);
                }
                else
                {
                    r = Quantum.ScaleToByte(row[off]);
                    g = Quantum.ScaleToByte(row[off + 1]);
                    b = Quantum.ScaleToByte(row[off + 2]);
                }

                // Forward YCoCg (inverse of JxlModular.InvRct custom==6).
                int co = r - b;
                int tmp = b + (co >> 1);
                int cg = g - tmp;
                int yy = tmp + (cg >> 1);
                int p = (y * w) + x;
                chan[0][p] = yy;
                chan[1][p] = co;
                chan[2][p] = cg;
            }
        }

        // A single group can cover an image up to GroupDim on both sides; larger images are tiled.
        bool single = w <= GroupDim && h <= GroupDim;
        int shift = single ? SmallestShift(Math.Max(w, h)) : GroupSizeShift;
        int groupDim = 128 << shift;

        byte[][] sections = single
            ? new[] { BuildModularSection(chan, w, h, nb) }
            : BuildMultiGroupSections(chan, w, h, nb, groupDim);

        var main = new JxlBitWriter();
        main.WriteBits(0xFF, 8);
        main.WriteBits(0x0A, 8);
        WriteSizeHeader(main, w, h);
        WriteImageMetadata(main);
        main.JumpToByteBoundary();
        WriteFrameHeader(main, shift);
        main.WriteBool(false); // permuted TOC = false
        main.JumpToByteBoundary();

        foreach (byte[] sec in sections)
        {
            main.WriteU32((uint)sec.Length, E.BitsOff(10, 0), E.BitsOff(14, 1024), E.BitsOff(22, 17408), E.BitsOff(30, 4211712));
        }

        main.JumpToByteBoundary();
        foreach (byte[] sec in sections)
        {
            main.AppendBytes(sec);
        }

        return main.ToArray();
    }

    private static int SmallestShift(int side)
    {
        int shift = 0;
        while ((128 << shift) < side && shift < GroupSizeShift)
        {
            shift++;
        }

        return shift;
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    private static void WriteSizeHeader(JxlBitWriter w, int width, int height)
    {
        w.WriteBool(false); // not "small"
        w.WriteU32((uint)height, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
        w.WriteBits(0, 3); // aspect ratio 0 => explicit width
        w.WriteU32((uint)width, E.BitsOff(9, 1), E.BitsOff(13, 1), E.BitsOff(18, 1), E.BitsOff(30, 1));
    }

    private static void WriteImageMetadata(JxlBitWriter w)
    {
        w.WriteBool(false); // not all_default
        w.WriteBool(false); // extra_fields = false
        w.WriteBool(false); // bit depth: not floating
        w.WriteU32(8, E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)); // 8 bits per sample
        w.WriteBool(true);  // modular_16bit_buffer_sufficient
        w.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1)); // num_extra_channels = 0
        w.WriteBool(false); // xyb_encoded = false
        w.WriteBool(true);  // colour encoding: all_default (sRGB RGB)
        w.WriteU64(0);      // extensions = none
        w.WriteBool(true);  // default_m (skip opsin / upsampling weights)
    }

    private static void WriteFrameHeader(JxlBitWriter w, int shift)
    {
        w.WriteBool(false);  // not all_default
        w.WriteBits(0, 2);   // frame_type = Regular
        w.WriteBits(1, 1);   // encoding = Modular
        w.WriteU64(0);       // flags = 0
        w.WriteBool(false);  // do_ycbcr = false (non-XYB)
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // upsampling = 1
        w.WriteBits((uint)shift, 2); // group_size_shift
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4)); // num_passes = 1
        w.WriteBool(false);  // have_crop = false
        w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3)); // blending mode = 0 (replace)
        w.WriteBool(true);   // is_last = true
        w.WriteU32(0, E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48)); // name length = 0

        // Loop filter: the all_default filter leaves Gaborish + EPF ON, which libjxl applies even to a
        // Modular frame and would blur a lossless image. Disable both explicitly.
        w.WriteBool(false);  // loop filter: not all_default
        w.WriteBool(false);  // gaborish = off
        w.WriteBits(0, 2);   // epf_iters = 0
        w.WriteU64(0);       // loop-filter extensions = none

        w.WriteU64(0);       // frame-header extensions = none
    }

    // --- Single-group frame: one section holding the tree, the pixel histogram and every pixel token
    // (DecodeGlobalModular). ---
    private static byte[] BuildModularSection(int[][] chan, int w, int h, int nb)
    {
        int[][] tokens = new int[nb][];
        int maxToken = 0;
        for (int c = 0; c < nb; c++)
        {
            tokens[c] = ComputeResidualTokens(chan[c], w, h, ref maxToken);
        }

        long[] pxFreq = new long[maxToken + 1];
        for (int c = 0; c < nb; c++)
        {
            foreach (int t in tokens[c])
            {
                pxFreq[t]++;
            }
        }

        var pxCode = new JxlPrefixCode(pxFreq, maxToken + 1);

        var s = new JxlBitWriter();
        s.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s.WriteBits(1, 1); // has_tree = 1
        WriteSingleLeafTree(s);
        WritePixelHistogram(s, pxCode);
        WriteGlobalGroupHeader(s);
        for (int c = 0; c < nb; c++)
        {
            int[] tc = tokens[c];
            for (int i = 0; i < tc.Length; i++)
            {
                pxCode.WriteSymbol(s, tc[i]);
            }
        }

        return s.ToArray();
    }

    // --- Multi-group frame: LfGlobal (tree + pixel histogram + global RCT), empty LfGroup/HfGlobal
    // sections, then one Modular-AC section per spatial group. All groups share the global pixel code;
    // each group predicts within its own tile (matching DecodeMultiGroup). ---
    private static byte[][] BuildMultiGroupSections(int[][] chan, int w, int h, int nb, int groupDim)
    {
        int gpr = CeilDiv(w, groupDim);
        int numGroups = gpr * CeilDiv(h, groupDim);
        int lfDim = groupDim * 8;
        int numLf = CeilDiv(w, lfDim) * CeilDiv(h, lfDim);

        int[][][] groupTokens = new int[numGroups][][];
        int maxToken = 0;
        for (int g = 0; g < numGroups; g++)
        {
            int rx = (g % gpr) * groupDim;
            int ry = (g / gpr) * groupDim;
            int rw = Math.Min(groupDim, w - rx);
            int rh = Math.Min(groupDim, h - ry);
            groupTokens[g] = new int[nb][];
            for (int c = 0; c < nb; c++)
            {
                groupTokens[g][c] = ComputeGroupResidualTokens(chan[c], w, rx, ry, rw, rh, ref maxToken);
            }
        }

        long[] pxFreq = new long[maxToken + 1];
        for (int g = 0; g < numGroups; g++)
        {
            for (int c = 0; c < nb; c++)
            {
                foreach (int t in groupTokens[g][c])
                {
                    pxFreq[t]++;
                }
            }
        }

        var pxCode = new JxlPrefixCode(pxFreq, maxToken + 1);

        // Section 0: LfGlobal.
        var s0 = new JxlBitWriter();
        s0.WriteBits(1, 1); // DequantMatrices::DecodeDC all_default
        s0.WriteBits(1, 1); // has_tree = 1
        WriteSingleLeafTree(s0);
        WritePixelHistogram(s0, pxCode);
        WriteGlobalGroupHeader(s0); // use_global + wp + RCT; no channels are small enough to decode here

        var list = new List<byte[]>(1 + numLf + 1 + numGroups) { s0.ToArray() };
        for (int i = 0; i < numLf; i++)
        {
            list.Add(Array.Empty<byte>()); // LfGroup: empty for a Modular frame
        }

        list.Add(Array.Empty<byte>()); // HfGlobal: empty for a Modular frame

        for (int g = 0; g < numGroups; g++)
        {
            var sg = new JxlBitWriter();
            sg.WriteBool(true); // use_global tree + code
            sg.WriteBits(1, 1); // weighted-predictor header: all default
            sg.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0
            for (int c = 0; c < nb; c++)
            {
                int[] tc = groupTokens[g][c];
                for (int i = 0; i < tc.Length; i++)
                {
                    pxCode.WriteSymbol(sg, tc[i]);
                }
            }

            list.Add(sg.ToArray());
        }

        return list.ToArray();
    }

    // Single-leaf global MA tree: property token 0 (=> leaf), then predictor / offset / mult-log /
    // mult-bits. The tree stream reads six contexts, all mapped to one histogram.
    private static void WriteSingleLeafTree(JxlBitWriter s)
    {
        long[] treeFreq = new long[WeightedPredictor + 1];
        treeFreq[0] = 4;                 // property(0), offset(0), mult-log(0), mult-bits(0)
        treeFreq[WeightedPredictor] = 1; // predictor
        var treeCode = new JxlPrefixCode(treeFreq, WeightedPredictor + 1);
        WriteHistogramsHeader(s, numContexts: 6, treeCode);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, WeightedPredictor);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, 0);
        treeCode.WriteSymbol(s, 0);
    }

    private static void WritePixelHistogram(JxlBitWriter s, JxlPrefixCode pxCode)
        => WriteHistogramsHeader(s, numContexts: 1, pxCode); // (treeCount+1)/2 == 1 context

    // GroupHeader for the global modular stream: use_global tree+code, default weighted-predictor
    // header, and one transform — the YCoCg RCT, inverted after decode.
    private static void WriteGlobalGroupHeader(JxlBitWriter s)
    {
        s.WriteBool(true);  // use_global tree + code
        s.WriteBits(1, 1);  // weighted-predictor header: all default
        s.WriteU32(1, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 1
        s.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.Val(3)); // transform id = 0 (RCT)
        s.WriteU32(0, E.BitsOff(3, 0), E.BitsOff(6, 8), E.BitsOff(10, 72), E.BitsOff(13, 1096)); // begin_c = 0
        s.WriteU32(6, E.Val(6), E.BitsOff(2, 0), E.BitsOff(4, 2), E.BitsOff(6, 10)); // rct_type = 6
    }

    // Self-correcting weighted-predictor (predictor 6) residuals, packed with the JXL signed->unsigned
    // mapping. The encoder runs the decoder's exact WpState so the predictions — and thus residuals —
    // match the decode bit-for-bit.
    private static int[] ComputeResidualTokens(int[] px, int w, int h, ref int maxToken)
    {
        int[] tokens = new int[w * h];
        int local = maxToken;
        var wp = new WpState(WpHeader.Default(), w);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                int top = y > 0 ? px[((y - 1) * w) + x] : left;
                int topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                int topright = (x + 1 < w && y > 0) ? px[((y - 1) * w) + x + 1] : top;
                int toptop = y > 1 ? px[((y - 2) * w) + x] : top;
                long guess = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
                int pixel = px[(y * w) + x];
                int residual = pixel - (int)guess;
                int token = (int)(uint)((residual << 1) ^ (residual >> 31)); // PackSigned
                tokens[(y * w) + x] = token;
                if (token > local)
                {
                    local = token;
                }

                wp.Update(pixel, x, y);
            }
        }

        maxToken = local;
        return tokens;
    }

    // WP residuals for one tile (rx,ry,rw,rh) of a full channel. Prediction is tile-local — pixels
    // outside the tile are treated as edges — exactly as DecodeMultiGroup decodes each group.
    private static int[] ComputeGroupResidualTokens(int[] full, int fullW, int rx, int ry, int rw, int rh, ref int maxToken)
    {
        int[] tokens = new int[rw * rh];
        int local = maxToken;
        var wp = new WpState(WpHeader.Default(), rw);
        int P(int xx, int yy) => full[((ry + yy) * fullW) + rx + xx];
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int left = x > 0 ? P(x - 1, y) : (y > 0 ? P(x, y - 1) : 0);
                int top = y > 0 ? P(x, y - 1) : left;
                int topleft = (x > 0 && y > 0) ? P(x - 1, y - 1) : left;
                int topright = (x + 1 < rw && y > 0) ? P(x + 1, y - 1) : top;
                int toptop = y > 1 ? P(x, y - 2) : top;
                long guess = wp.Predict(x, y, top, left, topright, topleft, toptop, null);
                int pixel = P(x, y);
                int residual = pixel - (int)guess;
                int token = (int)(uint)((residual << 1) ^ (residual >> 31));
                tokens[(y * rw) + x] = token;
                if (token > local)
                {
                    local = token;
                }

                wp.Update(pixel, x, y);
            }
        }

        maxToken = local;
        return tokens;
    }

    // DecodeHistograms mirror for a single-histogram, prefix-coded, LZ77-free entropy code.
    private static void WriteHistogramsHeader(JxlBitWriter w, int numContexts, JxlPrefixCode code)
    {
        w.WriteBool(false); // lz77 disabled
        if (numContexts > 1)
        {
            // Simple context map, bits-per-entry 0 => every context maps to cluster 0 (one histogram).
            w.WriteBool(true);
            w.WriteBits(0, 2);
        }

        w.WriteBool(true); // use_prefix_code
        WriteUintConfig(w, splitExp: 15, msb: 0, lsb: 0, logAlpha: 15);
        w.WriteVarLenUint16(code.AlphabetSize - 1);
        code.WriteHeader(w);
    }

    private static void WriteUintConfig(JxlBitWriter w, int splitExp, int msb, int lsb, int logAlpha)
    {
        w.WriteBits((uint)splitExp, JxlBits.CeilLog2(logAlpha + 1));
        if (splitExp != logAlpha)
        {
            w.WriteBits((uint)msb, JxlBits.CeilLog2(splitExp + 1));
            w.WriteBits((uint)lsb, JxlBits.CeilLog2(splitExp - msb + 1));
        }
    }
}
