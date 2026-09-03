// JPEG XL VarDCT (lossy) codestream writer — the exact inverse of the JxlVarDct decoder + JxlFrame
// frame parser. Emits a single-section XYB VarDCT frame (LfGlobal, one LfGroup, HfGlobal, one PassGroup)
// for images up to one group (256x256, since VarDCT group_size_shift defaults to 1). The forward
// transform math lives in JxlVarDctEncoder; this file produces the bitstream around it.
//
// Loop filters (Gaborish/EPF) and adaptive-LF smoothing are disabled via the frame header, and
// chroma-from-luma is left at its all-default correlation (X: +0*Y, B: +1*Y) with the B luma term
// pre-subtracted in the encoder — so the decoder's reconstruction equals JxlVarDctEncoder's validated
// forward/inverse model exactly.
//
// Three conformance points that cost real debugging: (1) ImageMetadata must be written out explicitly
// with xyb_encoded = true — the all_default metadata is read as non-XYB by libjxl/jxl-oxide, which then
// expect a do_ycbcr bit and desync; (2) the LfGroup modular sub-images must reference a *global* MA tree
// in LfGlobal (use_global_tree = true) — local per-image trees are rejected by both reference decoders;
// (3) Dct8 needs transposition (NeedTranspose(Dct8) == true) — the decoder places each coefficient at the
// transposed block position and dequantises with the transposed matrix, so the encoder must too.
// Output verified decodable by libjxl (ffmpeg) and jxl-oxide (which agree pixel-close), plus this repo's
// own decoder — whose reconstruction matches the validated forward model to ~0.05 dB.
using System;
using System.Collections.Generic;
using SharpImage.Core;
using SharpImage.Image;
using E = SharpImage.Formats.Jxl.JxlBitReader.U32Enc;

namespace SharpImage.Formats.Jxl;

internal static partial class JxlEncoder
{
    private const int VarDctGroupDim = 256;       // VarDCT group side (fixed)
    private const int VarDctLfGroupDim = 256 * 8; // 2048 — one LF group; larger needs numLf > 1 (todo)

    /// <summary>
    /// Encodes an image as a lossy XYB VarDCT JPEG XL codestream (single group, all-8x8 DCT).
    /// globalScale/quantLf/blockHfMul are the quantiser knobs (see JxlVarDctEncoder.ReconstructPsnr).
    /// <paramref name="passShifts"/> enables progressive decoding: it lists the AC coefficient left-shifts
    /// for the leading (coarse) passes in DECREASING order; a final full-precision (shift 0) pass is
    /// appended automatically. E.g. [2,1] => 3 passes with shifts {2,1,0}. Null/empty => a single pass.
    /// </summary>
    public static byte[] EncodeVarDct(ImageFrame image, uint globalScale = 4096, uint quantLf = 32, uint blockHfMul = 1, int[]? passShifts = null, bool adaptiveQuant = false, bool variableBlocks = false)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        if (w <= 0 || h <= 0)
        {
            throw new InvalidOperationException("Cannot encode an empty image.");
        }

        if (w > VarDctLfGroupDim || h > VarDctLfGroupDim)
        {
            throw new NotSupportedException("VarDCT encoder currently supports images up to 2048x2048 (one LF group).");
        }

        // Full per-pass shift list: the caller's coarse shifts followed by the mandatory final shift 0.
        int[] shifts = BuildPassShifts(passShifts);

        float[][] srgb = ExtractSrgb(image, w, h);
        List<byte[]> sections = BuildVarDctSections(srgb, w, h, globalScale, quantLf, blockHfMul, shifts, adaptiveQuant, variableBlocks);
        return AssembleVarDctCodestream(w, h, sections, shifts);
    }

    private static int[] BuildPassShifts(int[]? passShifts)
    {
        if (passShifts == null || passShifts.Length == 0)
        {
            return new[] { 0 };
        }

        for (int i = 0; i < passShifts.Length; i++)
        {
            if (passShifts[i] < 1 || passShifts[i] > 3)
            {
                throw new ArgumentException("Progressive pass shifts must be in 1..3 (2-bit field).");
            }

            if (i > 0 && passShifts[i] >= passShifts[i - 1])
            {
                throw new ArgumentException("Progressive pass shifts must be strictly decreasing.");
            }
        }

        var shifts = new int[passShifts.Length + 1];
        Array.Copy(passShifts, shifts, passShifts.Length); // final entry stays 0 (full precision)
        return shifts;
    }

    /// <summary>
    /// Whether the VarDCT (lossy) encoder can handle this image (single LF group, i.e. <= 2048x2048).
    /// </summary>
    public static bool CanEncodeVarDct(ImageFrame image) =>
        image.Columns > 0 && image.Rows > 0 && image.Columns <= VarDctLfGroupDim && image.Rows <= VarDctLfGroupDim;

    /// <summary>
    /// Encodes an image as a lossy VarDCT JPEG XL codestream at the given Butteraugli-style
    /// <paramref name="distance"/> (lower = higher quality; ~1.0 is high quality, larger is lower). The
    /// distance-to-quantiser mapping is a pragmatic approximation, not perceptually calibrated yet.
    /// </summary>
    public static byte[] EncodeVarDct(ImageFrame image, float distance, int[]? passShifts = null)
    {
        (uint gs, uint qlf, uint hfm) = QuantForDistance(distance);
        return EncodeVarDct(image, gs, qlf, hfm, passShifts, adaptiveQuant: true, variableBlocks: true);
    }

    // Maps a Butteraugli-style distance to (global_scale, quant_lf, block_hf_mul). global_scale is fixed;
    // block_hf_mul scales AC precision (~1/distance) and quant_lf scales DC precision, capped so the DC
    // integers stay inside the 16-bit modular buffer.
    private static (uint GlobalScale, uint QuantLf, uint BlockHfMul) QuantForDistance(float distance)
    {
        // Bases calibrated so distance 1.0 lands near libjxl's distance-1 quality (~39 dB) on detailed
        // content, rather than the near-lossless our earlier bases produced.
        float d = Math.Clamp(distance, 0.1f, 25f);
        uint hfm = (uint)Math.Clamp((int)MathF.Round(22f / d), 1, 4096);
        uint qlf = (uint)Math.Clamp((int)MathF.Round(90f / d), 1, 512);
        return (8192u, qlf, hfm);
    }

    /// <summary>Butteraugli-style distance for a JPEG-like quality in [0,100] (higher quality => lower distance).</summary>
    public static float DistanceFromQuality(int quality)
    {
        int q = Math.Clamp(quality, 1, 100);
        if (q >= 100)
        {
            return 0.1f; // we do not emit truly-lossless VarDCT; clamp to very high quality
        }

        return q >= 30
            ? 0.1f + ((100 - q) * 0.09f)
            : 6.4f + (MathF.Pow(2.5f, (30 - q) / 5f) * 0.09f);
    }

    // Extract sRGB [0,1] float channels (grayscale expanded to RGB), matching EncodeLossless's sampling.
    private static float[][] ExtractSrgb(ImageFrame image, int w, int h)
    {
        var srgb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            srgb[c] = new float[w * h];
        }

        int srcCh = image.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int off = x * srcCh;
                int p = (y * w) + x;
                if (srcCh == 1)
                {
                    float v = Quantum.ScaleToByte(row[off]) / 255f;
                    srgb[0][p] = srgb[1][p] = srgb[2][p] = v;
                }
                else
                {
                    srgb[0][p] = Quantum.ScaleToByte(row[off]) / 255f;
                    srgb[1][p] = Quantum.ScaleToByte(row[off + 1]) / 255f;
                    srgb[2][p] = Quantum.ScaleToByte(row[off + 2]) / 255f;
                }
            }
        }

        return srgb;
    }

    // --- codestream assembly (signature + size + metadata + frame header + TOC + sections) ---
    // For a single-group single-pass frame the whole body is one TOC section; a progressive (multi-pass)
    // frame uses the multi-section TOC (LfGlobal | LfGroup | HfGlobal | PassGroup-per-pass) so a streaming
    // decoder can render the DC preview from the LfGroup section before the AC passes arrive.
    private static byte[] AssembleVarDctCodestream(int w, int h, List<byte[]> sections, int[] shifts)
    {
        var main = new JxlBitWriter();
        main.WriteBits(0xFF, 8);
        main.WriteBits(0x0A, 8);
        WriteSizeHeader(main, w, h);
        WriteXybImageMetadata(main);
        main.JumpToByteBoundary();
        WriteVarDctFrameHeader(main, shifts);
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

    // Explicit ImageMetadata: 8-bit sRGB RGB, XYB-encoded, no extra channels, default opsin/upsampling.
    // Written out in full (not all_default) so xyb_encoded is unambiguously set for the decoder.
    private static void WriteXybImageMetadata(JxlBitWriter w)
    {
        w.WriteBool(false); // not all_default
        w.WriteBool(false); // extra_fields = false
        w.WriteBool(false); // bit depth: not floating
        w.WriteU32(8, E.Val(8), E.Val(10), E.Val(12), E.BitsOff(6, 1)); // 8 bits per sample
        w.WriteBool(true);  // modular_16bit_buffer_sufficient
        w.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(12, 1)); // num_extra_channels = 0
        w.WriteBool(true);  // xyb_encoded = true
        w.WriteBool(true);  // colour encoding: all_default (sRGB RGB)
        w.WriteU64(0);      // extensions = none
        w.WriteBool(true);  // default_m (skip opsin / upsampling weights — use XYB defaults)
    }

    // Frame header for a regular XYB VarDCT frame with loop filters + adaptive-LF-smoothing disabled.
    // shifts holds the per-pass coefficient shift (length == num_passes; final entry 0).
    private static void WriteVarDctFrameHeader(JxlBitWriter w, int[] shifts)
    {
        int numPasses = shifts.Length;
        w.WriteBool(false);  // not all_default
        w.WriteBits(0, 2);   // frame_type = Regular
        w.WriteBits(0, 1);   // encoding = VarDCT
        w.WriteU64(0x80);    // flags = kSkipAdaptiveDCSmoothing (0x80); no patches/splines/noise/useLf
        // md.Xyb => no do_ycbcr bit.
        w.WriteU32(1, E.Val(1), E.Val(2), E.Val(4), E.Val(8)); // upsampling = 1 (no extra channels)
        w.WriteBits(2, 3);   // x_qm_scale = 2 (default)
        w.WriteBits(2, 3);   // b_qm_scale = 2 (default)
        WritePasses(w, numPasses, shifts);
        w.WriteBool(false);  // have_crop = false
        w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(2, 3)); // blending mode = 0 (replace)
        w.WriteBool(true);   // is_last = true
        w.WriteU32(0, E.Val(0), E.BitsOff(4, 0), E.BitsOff(5, 16), E.BitsOff(10, 48)); // name length = 0

        w.WriteBool(false);  // loop filter: not all_default
        w.WriteBool(false);  // gaborish = off
        w.WriteBits(0, 2);   // epf_iters = 0
        w.WriteU64(0);       // loop-filter extensions = none
        w.WriteU64(0);       // frame-header extensions = none
    }

    // Passes bundle (JxlFrame.ReadPasses inverse): num_passes, then for >1 passes num_downsample=0 and the
    // (num_passes - 1) leading shift values (2 bits each; the final pass is implicitly shift 0).
    private static void WritePasses(JxlBitWriter w, int numPasses, int[] shifts)
    {
        w.WriteU32((uint)numPasses, E.Val(1), E.Val(2), E.Val(3), E.BitsOff(3, 4));
        if (numPasses != 1)
        {
            w.WriteU32(0, E.Val(0), E.Val(1), E.Val(2), E.BitsOff(1, 3)); // num_downsample = 0
            for (int i = 0; i < numPasses - 1; i++)
            {
                w.WriteBits((uint)shifts[i], 2);
            }
        }
    }

    // --- the frame body as TOC sections (see AssembleVarDctCodestream) ---
    private static List<byte[]> BuildVarDctSections(float[][] srgb, int w, int h, uint globalScale, uint quantLf, uint blockHfMul, int[] shifts, bool adaptiveQuant, bool variableBlocks)
    {
        int bw = (w + 7) / 8, bh = (h + 7) / 8;   // blocks
        int stride = bw * 8, strideH = bh * 8;
        int nbData = bw * bh;                      // all 8x8 => one data block each

        // Pad the source to whole blocks (edge-replicate), then convert to XYB.
        var padded = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            padded[c] = new float[stride * strideH];
            for (int y = 0; y < strideH; y++)
            {
                int sy = Math.Min(y, h - 1);
                for (int x = 0; x < stride; x++)
                {
                    padded[c][(y * stride) + x] = srgb[c][(sy * w) + Math.Min(x, w - 1)];
                }
            }
        }

        var fp = new VarDctFrameParams();
        float[][] xyb = JxlVarDctEncoder.SrgbToXyb(padded, stride * strideH, fp);

        // Per-block forward DCT + quantisation. Modular DC channels are [Y, X, B]; default CfL adds +1*Y
        // to B on decode (kx == 0, kb == 1), so B is stored with the dequantised Y term pre-subtracted
        // (both for the DC/LF value and every AC/HF coefficient); X is stored as-is.
        float[] mlf = { 1f / 32f, 1f / 4f, 1f / 2f };                 // m_x, m_y, m_b
        double scaleInv = (double)globalScale * quantLf;
        float scaleX = (float)(mlf[0] * 512.0 / scaleInv);
        float scaleY = (float)(mlf[1] * 512.0 / scaleInv);
        float scaleB = (float)(mlf[2] * 512.0 / scaleInv);
        DequantMatrixSet dm = DequantMatrixSet.Default();

        // Per-8x8-block luma HF activity (libjxl's HfModulation term): drives adaptive quant AND variable
        // block-size selection.
        double[] activity = ComputeActivity(xyb[1], stride, bw, bh);
        int[] hfMulBlock = adaptiveQuant ? AdaptiveHfMulFromActivity(activity, bw, bh, blockHfMul) : null!;

        // Block-size layout: sizeAt[pos] = (int)TransformType for a data block's top-left position, or -1
        // for a position covered by a larger block. Off => every 8x8 position is its own Dct8 block.
        int[] sizeAt = BuildDctLayout(xyb, stride, dm, globalScale, hfMulBlock, blockHfMul, adaptiveQuant, fp, bw, bh, variableBlocks);

        // Chroma-from-luma: per 64x64 cell, decorrelate X and B from Y (the AC coefficients). kxRaw/kbRaw
        // are the modular grid values in HfMetadata; the decoder applies X += (kxRaw/84)*Y and
        // B += (1 + kbRaw/84)*Y to the dequantized AC. We compute the optimal per-cell factor from the
        // mean-subtracted spatial correlation (== AC coefficient correlation by Parseval) and subtract the
        // *quantized* factor times the dequantized Y so encoder and decoder agree exactly.
        int cfW = (w + 63) / 64, cfH = (h + 63) / 64;
        (int[] cflKx, int[] cflKb) = ComputeCfLGrids(xyb, stride, w, h, cfW, cfH);

        var dcY = new int[nbData];
        var dcX = new int[nbData];
        var dcB = new int[nbData];
        var blockHfMuls = new int[nbData];   // per data-block-position hf_mul (for block_info + dequant)
        // AC coefficients laid out per position by scan index: ac[c][pos][oi] (oi < numBlocks
        // are the LF, not stored here). Jagged per data-block position => only allocates what's used, so
        // large transforms don't blow up memory. ac[c][pos] is null for covered / non-data positions.
        var ac = new int[3][][];
        for (int c = 0; c < 3; c++)
        {
            ac[c] = new int[nbData][];
        }

        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int bi = (by * bw) + bx;
                if (sizeAt[bi] < 0)
                {
                    continue; // covered by a larger block whose top-left was already processed
                }

                var t = (TransformType)sizeAt[bi];
                var (dw, dh) = JxlDct.DctSelectSize(t);   // in 8-blocks: Dct8 (1,1), Dct16 (2,2)
                int pw = dw * 8, ph = dh * 8;              // pixels
                int numBlocks = dw * dh;
                bool needTr = JxlDct.NeedTranspose(t);
                (ushort X, ushort Y)[] order = JxlVarDctTables.NaturalOrder(JxlDct.OrderId(t));
                int bHfMul = adaptiveQuant ? hfMulBlock[bi] : (int)blockHfMul;
                blockHfMuls[bi] = bHfMul;
                float hfMul = 65536.0f / (globalScale * (float)bHfMul);

                // Forward DCT of each channel's (pw x ph) block.
                var coeff = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    coeff[c] = new float[pw * ph];
                    var g = new JxlDct.Grid(coeff[c], 0, pw, pw, ph);
                    for (int yy = 0; yy < ph; yy++)
                    {
                        for (int xx = 0; xx < pw; xx++)
                        {
                            g.Set(xx, yy, xyb[c][(((by * 8) + yy) * stride) + (bx * 8) + xx]);
                        }
                    }

                    JxlDct.Dct2D(g, false);
                }

                // LF -> DC image. Invert TransformVarblock: dcVals = IDCT_(dw x dh)( coeff_lf * ScaleF ).
                // For Dct8 this reduces to dcVals = coeff[0] (1x1 IDCT and ScaleF are identities).
                int logbw = System.Numerics.BitOperations.TrailingZeroCount(dw);
                int logbh = System.Numerics.BitOperations.TrailingZeroCount(dh);
                var lf = new float[3][];
                for (int c = 0; c < 3; c++)
                {
                    lf[c] = new float[numBlocks];
                    var lg = new JxlDct.Grid(lf[c], 0, dw, dw, dh);
                    for (int ly = 0; ly < dh; ly++)
                    {
                        for (int lx = 0; lx < dw; lx++)
                        {
                            lg.Set(lx, ly, coeff[c][(ly * pw) + lx] * JxlDct.ScaleFactor(ly, 5 - logbh) * JxlDct.ScaleFactor(lx, 5 - logbw));
                        }
                    }

                    JxlDct.Dct2D(lg, true); // inverse -> dcVals in raster (dw x dh)
                }

                for (int ly = 0; ly < dh; ly++)
                {
                    for (int lx = 0; lx < dw; lx++)
                    {
                        int p2 = ((by + ly) * bw) + bx + lx;
                        int li = (ly * dw) + lx;
                        int yDcInt = (int)MathF.Round(lf[1][li] / scaleY);
                        dcY[p2] = yDcInt;
                        dcX[p2] = (int)MathF.Round(lf[0][li] / scaleX);
                        dcB[p2] = (int)MathF.Round((lf[2][li] - (yDcInt * scaleY)) / scaleB);
                    }
                }

                // HF: scan positions numBlocks.. in the block's natural order, transposed placement.
                int numCoeffs = numBlocks * 64;
                ac[0][bi] = new int[numCoeffs];
                ac[1][bi] = new int[numCoeffs];
                ac[2][bi] = new int[numCoeffs];
                float[] matX = dm.GetTransposed(0, t), matY = dm.GetTransposed(1, t), matB = dm.GetTransposed(2, t);
                int cell = ((by / 8) * cfW) + (bx / 8);   // 64px CfL cell for this block (blocks are <=64px, aligned)
                float kx = cflKx[cell] / 84.0f;           // BaseCorrelationX = 0
                float kb = 1.0f + (cflKb[cell] / 84.0f);   // BaseCorrelationB = 1
                for (int oi = numBlocks; oi < order.Length; oi++)
                {
                    int k = needTr ? (order[oi].X * pw) + order[oi].Y : (order[oi].Y * pw) + order[oi].X;
                    int qY = QuantAc(coeff[1][k], matY[k] * hfMul, fp.QuantBias[1], fp.QuantBiasNumerator);
                    ac[1][bi][oi] = qY;
                    float yDeq = DequantAc(qY, matY[k] * hfMul, fp.QuantBias[1], fp.QuantBiasNumerator);
                    ac[0][bi][oi] = QuantAc(coeff[0][k] - (kx * yDeq), matX[k] * hfMul, fp.QuantBias[0], fp.QuantBiasNumerator);
                    ac[2][bi][oi] = QuantAc(coeff[2][k] - (kb * yDeq), matB[k] * hfMul, fp.QuantBias[2], fp.QuantBiasNumerator);
                }
            }
        }

        // Build the LfGroup modular sub-images. libjxl (and jxl-oxide) require a *global* MA tree in
        // LfGlobal that every sub-image references (use_global_tree = true); local per-image trees are
        // rejected. We use a single-leaf ClampedGradient tree and one shared histogram over all channels
        // of both sub-images.
        var dcImage = new List<SubChannel> { new(dcY, bw, bh), new(dcX, bw, bh), new(dcB, bw, bh) };

        // Block info is one entry per DATA block in raster visitation order (skipping covered positions):
        // row0 = dct_select, row1 = hf_mul - 1. This is also the order the HF pass codes coefficients in.
        var dataBlocks = new List<int>();
        for (int pos = 0; pos < nbData; pos++)
        {
            if (sizeAt[pos] >= 0)
            {
                dataBlocks.Add(pos);
            }
        }

        int nbBlocks = dataBlocks.Count;
        var blockInfo = new int[nbBlocks * 2];
        for (int di = 0; di < nbBlocks; di++)
        {
            int pos = dataBlocks[di];
            blockInfo[di] = sizeAt[pos];                     // dct_select
            blockInfo[nbBlocks + di] = blockHfMuls[pos] - 1; // hf_mul - 1
        }

        var hfMeta = new List<SubChannel>
        {
            new(cflKx, cfW, cfH),                  // x_from_y (per-cell CfL)
            new(cflKb, cfW, cfH),                  // b_from_y (per-cell CfL)
            new(blockInfo, nbBlocks, 2),           // block info
            new(new int[bw * bh], bw, bh),         // sharpness (all 0)
        };

        // The DC image (3 channels) and HfMetadata (4 channels) are two sub-modular streams that share one
        // global MA tree in LfGlobal. Encode them with the (libjxl-matching) lossless modular machinery: a
        // learned tree over both streams' channels + the weighted predictor + clustered ANS histograms.
        // Decoder stream ids (== group property): DC = 1 + lfGroupIdx, HfMetadata = 1 + 2*numLf + lfGroupIdx
        // (numLf == 1, lfGroupIdx == 0).
        const int dcStreamId = 1, metaStreamId = 3, dcWpMode = 0;
        WpHeader dcWp = WpMode(dcWpMode);
        var dcChans = new List<EncChannel> { new(dcY, bw, bh), new(dcX, bw, bh), new(dcB, bw, bh) };
        var metaChans = new List<EncChannel>
        {
            new(cflKx, cfW, cfH),
            new(cflKb, cfW, cfH),
            new(blockInfo, nbBlocks, 2),
            new(new int[bw * bh], bw, bh),
        };

        var learnRefs = new List<EncChannelRef>();
        for (int c = 0; c < dcChans.Count; c++)
        {
            learnRefs.Add(new EncChannelRef(dcChans[c].Data, dcChans[c].W, dcChans[c].H, c, dcStreamId, RefsFor(dcChans, c)));
        }

        for (int c = 0; c < metaChans.Count; c++)
        {
            learnRefs.Add(new EncChannelRef(metaChans[c].Data, metaChans[c].W, metaChans[c].H, c, metaStreamId, RefsFor(metaChans, c)));
        }

        var dcTree = new LearnedTree(JxlTreeLearner.Learn(learnRefs, dcWp, DcNodeThreshold));

        int dcLen = 0;
        foreach (EncChannel ch in dcChans)
        {
            dcLen += ch.W * ch.H;
        }

        int metaLen = 0;
        foreach (EncChannel ch in metaChans)
        {
            metaLen += ch.W * ch.H;
        }

        var dcStream = new int[dcLen];
        var dcCtx = new int[dcLen];
        var metaStream = new int[metaLen];
        var metaCtx = new int[metaLen];
        int dcMaxTok = 0, dcOff = 0;
        for (int c = 0; c < dcChans.Count; c++)
        {
            ComputeResidualTokensCtx(dcChans[c], c, dcTree, dcStream, dcCtx, dcOff, ref dcMaxTok, dcWp, RefsFor(dcChans, c), dcStreamId);
            dcOff += dcChans[c].W * dcChans[c].H;
        }

        int metaOff = 0;
        for (int c = 0; c < metaChans.Count; c++)
        {
            ComputeResidualTokensCtx(metaChans[c], c, dcTree, metaStream, metaCtx, metaOff, ref dcMaxTok, dcWp, RefsFor(metaChans, c), metaStreamId);
            metaOff += metaChans[c].W * metaChans[c].H;
        }

        var dcStreams = new List<(int[], int[])> { (dcStream, dcCtx), (metaStream, metaCtx) };
        PixelPlanCtx dcPlan = PlanPixelsCtx(dcStreams, dcTree.LeafCount, out List<Op>[] dcOps, int.MaxValue / 2);
        int dcLogAlpha = Math.Max(5, JxlBits.CeilLog2(dcPlan.LitAlphabet));
        bool useLearnedDc = dcLogAlpha <= 8; // ANS alphabet limit; else fall back to the gradient path
        int[][] dcAnsCounts = null!;
        JxlAnsWriter dcAns = null!;
        if (useLearnedDc)
        {
            dcAnsCounts = new int[dcPlan.K][];
            for (int c = 0; c < dcPlan.K; c++)
            {
                dcAnsCounts[c] = JxlEntropy.NormalizeCounts(dcPlan.ClusterHist[c], JxlEntropy.HistShift);
            }

            dcAns = new JxlAnsWriter(dcAnsCounts, dcLogAlpha);
        }

        // Fallback (rare, very fine quant): the earlier single-leaf gradient + one-histogram sub-images.
        List<ModToken> dcTokens = useLearnedDc ? null! : GradientTokens(dcImage);
        List<ModToken> metaTokens = useLearnedDc ? null! : GradientTokens(hfMeta);
        var modHist = new long[1];
        int modLogAlpha = 5;
        int[] modNorm = null!;
        var fallbackLeaf = new LearnedTree(new MaTreeNode { Property = -1, Predictor = 5 });
        if (!useLearnedDc)
        {
            AccumulateHist(dcTokens, ref modHist);
            AccumulateHist(metaTokens, ref modHist);
            modLogAlpha = Math.Max(5, JxlBits.CeilLog2(modHist.Length));
            modNorm = JxlEntropy.NormalizeCounts(modHist, JxlEntropy.HistShift);
        }

        // Group layout: 256px groups (32 blocks). numLf == 1 for images <= 2048px (one LF group).
        const int groupBlocks = VarDctGroupDim / 8; // 32
        int groupsPerRow = (w + VarDctGroupDim - 1) / VarDctGroupDim;
        int groupsPerCol = (h + VarDctGroupDim - 1) / VarDctGroupDim;
        int numGroups = groupsPerRow * groupsPerCol;
        int presetBits = BitLength(NextPow2(numGroups)); // hf_preset selector width

        // Split the full AC coefficients into progressive passes: pass p codes (remaining >> shift[p]) and
        // the decoder accumulates (value << shift[p]); the shifts decrease to a final 0 so the sum is exact.
        // Within a pass, each 256px group's coefficients are a separate PassGroup ANS stream that shares the
        // pass's HfDist histogram.
        const int hfNumContexts = 495 * DefaultNumBlockClusters; // 7425
        int numPasses = shifts.Length;
        var passGroupTokens = new List<ModToken>[numPasses][];     // [pass][group]
        var passGroupCtxs = new List<int>[numPasses][];            // [pass][group]
        var passClusterNorm = new int[numPasses][][];              // [pass][cluster][alphabet]
        var passMap = new int[numPasses][];                        // [pass][context] -> cluster
        var passLogAlpha = new int[numPasses];
        var remaining = new int[3][][];
        for (int c = 0; c < 3; c++)
        {
            remaining[c] = new int[nbData][];
            for (int bi = 0; bi < nbData; bi++)
            {
                if (ac[c][bi] != null)
                {
                    remaining[c][bi] = (int[])ac[c][bi].Clone();
                }
            }
        }

        for (int p = 0; p < numPasses; p++)
        {
            int sp = shifts[p];
            var qp = new int[3][][];
            for (int c = 0; c < 3; c++)
            {
                qp[c] = new int[nbData][];
                for (int bi = 0; bi < nbData; bi++)
                {
                    int[] rem = remaining[c][bi];
                    if (rem == null)
                    {
                        continue;
                    }

                    var qb = new int[rem.Length];
                    for (int oi = 0; oi < rem.Length; oi++)
                    {
                        int q = rem[oi] >> sp; // arithmetic shift (floor) — matches decoder's << sp
                        qb[oi] = q;
                        rem[oi] -= q << sp;
                    }

                    qp[c][bi] = qb;
                }
            }

            passGroupTokens[p] = new List<ModToken>[numGroups];
            passGroupCtxs[p] = new List<int>[numGroups];
            int maxSym = 0;
            for (int g = 0; g < numGroups; g++)
            {
                int gx = g % groupsPerRow, gy = g / groupsPerRow;
                int bx0 = gx * groupBlocks, by0 = gy * groupBlocks;
                int gBw = Math.Min(groupBlocks, bw - bx0);
                int gBh = Math.Min(groupBlocks, bh - by0);
                var ctxs = new List<int>();
                var toks = new List<ModToken>();
                BuildHfTokensCtx(bx0, by0, gBw, gBh, bw, qp, sizeAt, ctxs, toks);
                passGroupCtxs[p][g] = ctxs;
                passGroupTokens[p][g] = toks;
                foreach (ModToken t in toks)
                {
                    maxSym = Math.Max(maxSym, t.Sym);
                }
            }

            int alphabet = maxSym + 1;
            passLogAlpha[p] = Math.Max(5, JxlBits.CeilLog2(alphabet));

            // Per-context histograms over every group of this pass, then cluster into a small set.
            var ctxHist = new long[hfNumContexts][];
            for (int i = 0; i < hfNumContexts; i++)
            {
                ctxHist[i] = new long[alphabet];
            }

            for (int g = 0; g < numGroups; g++)
            {
                List<int> ctxs = passGroupCtxs[p][g];
                List<ModToken> toks = passGroupTokens[p][g];
                for (int i = 0; i < toks.Count; i++)
                {
                    ctxHist[ctxs[i]][toks[i].Sym]++;
                }
            }

            passMap[p] = ClusterContexts(ctxHist, alphabet, MaxHfClusters, out long[][] clusterHist, out int k);
            passClusterNorm[p] = new int[k][];
            for (int cIdx = 0; cIdx < k; cIdx++)
            {
                passClusterNorm[p][cIdx] = JxlEntropy.NormalizeCounts(clusterHist[cIdx], JxlEntropy.HistShift);
            }
        }

        int nbBits = BitLength(NextPow2(bw * bh));

        void WriteLfGlobal(JxlBitWriter b)
        {
            b.WriteBool(true);  // lf_channel_dequant all_default {1/32, 1/4, 1/2}
            b.WriteU32(globalScale, E.BitsOff(11, 1), E.BitsOff(11, 2049), E.BitsOff(12, 4097), E.BitsOff(16, 8193));
            b.WriteU32(quantLf, E.Val(16), E.BitsOff(5, 1), E.BitsOff(8, 1), E.BitsOff(16, 1));
            b.WriteBool(true);  // hf_block_context all_default (15 clusters, default map)
            b.WriteBool(true);  // lf_channel_correlation all_default (base_x 0, base_b 1)
            b.WriteBits(1, 1);  // GlobalModular: has_tree = 1 (shared tree for the DC + metadata sub-images)
            if (useLearnedDc)
            {
                WriteTree(b, dcTree.Tokens);
                WriteAnsHistogramCtx(b, dcPlan, dcAnsCounts, dcLogAlpha);
            }
            else
            {
                WriteTree(b, fallbackLeaf.Tokens);
                WriteSingleContextAnsHeader(b, modNorm, modLogAlpha);
            }
        }

        void WriteLfGroup(JxlBitWriter b)
        {
            b.WriteBits(0, 2);  // extra_precision = 0
            if (useLearnedDc)
            {
                WriteSubModularOps(b, dcOps[0], dcCtx, dcPlan, dcAns, dcWpMode);
                b.WriteBits((uint)(nbBlocks - 1), nbBits); // HfMetadata num_blocks
                WriteSubModularOps(b, dcOps[1], metaCtx, dcPlan, dcAns, dcWpMode);
            }
            else
            {
                WriteSubModularUsingGlobal(b, dcTokens, modNorm, modLogAlpha);
                b.WriteBits((uint)(nbBlocks - 1), nbBits);
                WriteSubModularUsingGlobal(b, metaTokens, modNorm, modLogAlpha);
            }
        }

        void WriteHfGlobal(JxlBitWriter b)
        {
            b.WriteBool(true);  // DequantMatrixSet all_default
            b.WriteBits(0, presetBits); // num_hf_presets - 1 == 0 (one preset)
            for (int p = 0; p < numPasses; p++)
            {
                WriteHfPass(b, passMap[p], passClusterNorm[p], passLogAlpha[p]);
            }
        }

        void WritePassGroup(JxlBitWriter b, int p, int g)
        {
            // hf_preset selector width = BitLength(NextPow2(num_hf_presets)); num_hf_presets == 1 => 0 bits.
            var ans = new JxlAnsWriter(passClusterNorm[p], passLogAlpha[p]);
            List<ModToken> toks = passGroupTokens[p][g];
            List<int> ctxs = passGroupCtxs[p][g];
            var ansToks = new List<AnsToken>(toks.Count);
            for (int i = 0; i < toks.Count; i++)
            {
                ModToken t = toks[i];
                ansToks.Add(new AnsToken(passMap[p][ctxs[i]], t.Sym, t.Bits, t.N));
            }

            ans.Encode(b, ansToks);
        }

        if (numGroups == 1 && numPasses == 1)
        {
            // Single group + single pass => one continuous TOC section.
            var body = new JxlBitWriter();
            WriteLfGlobal(body);
            WriteLfGroup(body);
            WriteHfGlobal(body);
            WritePassGroup(body, 0, 0);
            return new List<byte[]> { body.ToArray() };
        }

        // Multi-section TOC: LfGlobal | LfGroup (numLf == 1) | HfGlobal | PassGroups. Section index for a
        // pass group is 2 + numLf + pass*numGroups + group, so emit pass-major.
        var sections = new List<byte[]>();
        var lfg = new JxlBitWriter();
        WriteLfGlobal(lfg);
        sections.Add(lfg.ToArray());
        var lgr = new JxlBitWriter();
        WriteLfGroup(lgr);
        sections.Add(lgr.ToArray());
        var hfg = new JxlBitWriter();
        WriteHfGlobal(hfg);
        sections.Add(hfg.ToArray());
        for (int p = 0; p < numPasses; p++)
        {
            for (int g = 0; g < numGroups; g++)
            {
                var pg = new JxlBitWriter();
                WritePassGroup(pg, p, g);
                sections.Add(pg.ToArray());
            }
        }

        return sections;
    }

    private static void AccumulateHist(List<ModToken> tokens, ref long[] hist)
    {
        int max = hist.Length - 1;
        foreach (ModToken t in tokens)
        {
            max = Math.Max(max, t.Sym);
        }

        if (max + 1 > hist.Length)
        {
            Array.Resize(ref hist, max + 1);
        }

        foreach (ModToken t in tokens)
        {
            hist[t.Sym]++;
        }
    }

    private const int MaxHfClusters = 64; // cap on distinct HfDist histograms per pass

    // One HF pass: used_orders = 0 (natural order), then the HfDist entropy code — a context map over the
    // 495*num_block_clusters contexts to `k` clustered ANS histograms.
    private static void WriteHfPass(JxlBitWriter body, int[] contextToCluster, int[][] clusterNorm, int hfLogAlpha)
    {
        body.WriteU32(0, E.Val(0x5F), E.Val(0x13), E.Val(0x00), E.BitsOff(13, 0)); // used_orders = 0
        int k = clusterNorm.Length;
        body.WriteBool(false);   // lz77 disabled
        WriteContextMap(body, (int[])contextToCluster.Clone(), k);
        body.WriteBool(false);   // use_prefix_code = false (ANS)
        body.WriteBits((uint)(hfLogAlpha - 5), 2);
        for (int c = 0; c < k; c++)
        {
            WriteUintConfig(body, LitSplit, LitMsb, LitLsb, hfLogAlpha);
        }

        for (int c = 0; c < k; c++)
        {
            JxlEntropy.WriteHistogram(body, clusterNorm[c], JxlEntropy.HistShift);
        }
    }

    private const int DefaultNumBlockClusters = 15;

    private static readonly byte[] DefaultBlockCtxMap =
    {
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14,
        14, 14, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    };

    private static readonly uint[] CoeffFreqContext =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19,
        20, 20, 21, 21, 22, 22, 23, 23, 23, 23, 24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27,
        27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30,
    };

    private static readonly uint[] CoeffNumNonzeroContext =
    {
        0, 31, 62, 62, 93, 93, 93, 93, 123, 123, 123, 123, 152, 152, 152, 152, 152, 152, 152, 152,
        180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 180, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206, 206,
        206, 206, 206, 206, 206, 206, 206,
    };

    // Builds the PassGroup HF tokens for one 256px group WITH their entropy context id (0..495*15-1),
    // replicating JxlVarDct.WriteHfCoeff's context model exactly (block context, non-zeros prediction,
    // coefficient frequency / running-non-zeros / prev-non-zero). Layout-aware: each data block uses its
    // own dct_select (num_blocks, order_id, coefficient count); covered positions are skipped.
    private static void BuildHfTokensCtx(int bx0, int by0, int gBw, int gBh, int bw, int[][][] qp, int[] sizeAt, List<int> ctxsOut, List<ModToken> toksOut)
    {
        const int nbc = DefaultNumBlockClusters;
        var nonZerosGrid = new uint[3][];
        for (int c = 0; c < 3; c++)
        {
            nonZerosGrid[c] = new uint[gBw];
        }

        for (int ly = 0; ly < gBh; ly++)
        {
            for (int lx = 0; lx < gBw; lx++)
            {
                int pos = ((by0 + ly) * bw) + bx0 + lx;
                if (sizeAt[pos] < 0)
                {
                    continue; // covered by a larger block
                }

                var t = (TransformType)sizeAt[pos];
                var (dw, dh) = JxlDct.DctSelectSize(t);
                int numBlocks = dw * dh;
                int numBlocksLog = System.Numerics.BitOperations.TrailingZeroCount(numBlocks);
                int orderId = JxlDct.OrderId(t);
                int numCoeffs = numBlocks * 64;

                for (int cc = 0; cc < 3; cc++)
                {
                    int c = new[] { 1, 0, 2 }[cc]; // Y, X, B
                    int[] qb = qp[c][pos];
                    int blockCtx = DefaultBlockCtxMap[(cc * 13) + orderId]; // lf_idx/hf_idx 0
                    uint predicted = ly == 0
                        ? (lx == 0 ? 32u : nonZerosGrid[c][lx - 1])
                        : (lx == 0 ? nonZerosGrid[c][lx] : (nonZerosGrid[c][lx] + nonZerosGrid[c][lx - 1] + 1) >> 1);
                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = blockCtx + (int)(nzIdx * nbc);

                    int nonZeros = 0, lastNz = 0;
                    for (int oi = numBlocks; oi < numCoeffs; oi++)
                    {
                        if (qb[oi] != 0)
                        {
                            nonZeros++;
                            lastNz = oi;
                        }
                    }

                    ctxsOut.Add(nonZerosCtx);
                    toksOut.Add(HybridToken(nonZeros));
                    uint nonZerosVal = (uint)((nonZeros + numBlocks - 1) >> numBlocksLog);
                    for (int dx = 0; dx < dw; dx++)
                    {
                        nonZerosGrid[c][lx + dx] = nonZerosVal;
                    }

                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    uint isPrevNonzero = nonZeros <= numBlocks * 4 ? 1u : 0u;
                    int coeffCtxBase = (blockCtx * 458) + (37 * nbc);
                    int rem = nonZeros;
                    for (int oi = numBlocks; oi <= lastNz; oi++)
                    {
                        int fidx = oi - numBlocks;
                        int nzForCtx = (rem - 1) >> numBlocksLog;
                        int fq = fidx >> numBlocksLog;
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[nzForCtx] + CoeffFreqContext[fq]) * 2) + isPrevNonzero);
                        int ctx = coeffCtxBase + coeffCtx;
                        int q = qb[oi];
                        ctxsOut.Add(ctx);
                        if (q == 0)
                        {
                            toksOut.Add(HybridToken(0));
                            isPrevNonzero = 0;
                            continue;
                        }

                        toksOut.Add(HybridToken(PackSigned(q)));
                        isPrevNonzero = 1;
                        rem--;
                        if (rem == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    private static ModToken HybridToken(int value)
    {
        (int token, int nbits, int bits) = PackHybridFull(LitSplit, LitMsb, LitLsb, value);
        return new ModToken(token, (uint)bits, nbits);
    }

    // Per-8x8-block luma HF activity (libjxl's HfModulation term): sum of clamped |neighbour Y differences|.
    // High => busy/textured, low => smooth. Drives adaptive quant and variable block-size selection.
    // Chroma-from-luma factors per 64x64 cell. kxRaw = round(84 * cov(X,Y)/var(Y)); kbRaw =
    // round(84 * (cov(B,Y)/var(Y) - 1)) (base_correlation_b = 1). The mean-subtracted spatial correlation
    // over the cell equals the AC-coefficient correlation (Parseval), which is what the decoder's
    // ChromaFromLumaHf applies. Clamped so |k| stays sane; a suboptimal factor still round-trips exactly.
    private static (int[] kx, int[] kb) ComputeCfLGrids(float[][] xyb, int stride, int w, int h, int cfW, int cfH)
    {
        var kxG = new int[cfW * cfH];
        var kbG = new int[cfW * cfH];
        for (int cy = 0; cy < cfH; cy++)
        {
            int y0 = cy * 64, y1 = Math.Min(y0 + 64, h);
            for (int cx = 0; cx < cfW; cx++)
            {
                int x0 = cx * 64, x1 = Math.Min(x0 + 64, w);
                double sX = 0, sY = 0, sB = 0, sXY = 0, sYY = 0, sBY = 0;
                int n = 0;
                for (int py = y0; py < y1; py++)
                {
                    int rowOff = py * stride;
                    for (int px = x0; px < x1; px++)
                    {
                        int i = rowOff + px;
                        double X = xyb[0][i], Y = xyb[1][i], B = xyb[2][i];
                        sX += X; sY += Y; sB += B;
                        sXY += X * Y; sYY += Y * Y; sBY += B * Y;
                        n++;
                    }
                }

                if (n == 0)
                {
                    continue; // leaves kx=0, kb-adjust=0 (i.e. identity X, kb=1 B)
                }

                double ym = sY / n;
                double varY = (sYY / n) - (ym * ym);
                if (varY > 1e-8)
                {
                    double kx = ((sXY / n) - ((sX / n) * ym)) / varY;
                    double kb = ((sBY / n) - ((sB / n) * ym)) / varY;
                    kxG[(cy * cfW) + cx] = Math.Clamp((int)Math.Round(kx * 84.0), -128, 127);
                    kbG[(cy * cfW) + cx] = Math.Clamp((int)Math.Round((kb - 1.0) * 84.0), -128, 127);
                }
            }
        }

        return (kxG, kbG);
    }

    private static double[] ComputeActivity(float[] y, int stride, int bw, int bh)
    {
        const float valmin = 0.0206f;
        var act = new double[bw * bh];
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                double sum = 0;
                for (int yy = 0; yy < 8; yy++)
                {
                    for (int xx = 0; xx < 8; xx++)
                    {
                        int p = (((by * 8) + yy) * stride) + (bx * 8) + xx;
                        if (xx < 7)
                        {
                            sum += Math.Min(valmin, Math.Abs(y[p] - y[p + 1]));
                        }

                        if (yy < 7)
                        {
                            sum += Math.Min(valmin, Math.Abs(y[p] - y[p + stride]));
                        }
                    }
                }

                act[(by * bw) + bx] = sum;
            }
        }

        return act;
    }

    // hf_mul per block from activity: busy => coarser (smaller), smooth => finer, geometric mean pinned to
    // the distance-calibrated base so the average rate is preserved (libjxl's masking direction).
    private static int[] AdaptiveHfMulFromActivity(double[] act, int bw, int bh, uint baseHfMul)
    {
        const double kMul = -0.38;
        int nb = bw * bh;
        double meanAct = 0;
        for (int i = 0; i < nb; i++)
        {
            meanAct += act[i];
        }

        meanAct /= nb;
        var mul = new int[nb];
        int cap = (int)baseHfMul * 4;
        for (int i = 0; i < nb; i++)
        {
            mul[i] = Math.Clamp((int)Math.Round(baseHfMul * Math.Exp(kMul * (act[i] - meanAct))), 1, cap);
        }

        return mul;
    }

    // Block-size layout via a simple rate-distortion check: for each aligned in-bounds 2x2 region, quantise
    // it both as one Dct16 and as four Dct8 (same hf_mul so distortion is comparable) and count luma AC
    // non-zeros; choose Dct16 only when it produces fewer coefficients (concentrated energy => smooth
    // region), which is where it actually saves bits. -1 marks positions covered by a larger block.
    private static int[] BuildDctLayout(float[][] xyb, int stride, DequantMatrixSet dm, uint globalScale, int[] hfMulBlock, uint blockHfMul, bool adaptiveQuant, VarDctFrameParams fp, int bw, int bh, bool variableBlocks)
    {
        var sizeAt = new int[bw * bh];
        for (int i = 0; i < sizeAt.Length; i++)
        {
            sizeAt[i] = (int)TransformType.Dct8;
        }

        if (!variableBlocks)
        {
            return sizeAt;
        }

        const int perBlockPenalty = 4;      // ~ non-zeros-count tokens + block_info per data block
        const int rectMargin = 3;           // rectangular must beat the best square split by this margin
        const int dct32Margin = 8;          // Dct32 (very large) must clearly beat the 16 8x8 split

        int Count(int x, int y, TransformType t, int hfMul) => CountLumaAcNonzeros(xyb[1], stride, x, y, t, dm, globalScale, hfMul, fp);

        // Assigns a square block of `side` 8x8 positions (its top-left = type t, the rest -1) iff it clearly
        // beats splitting into side*side Dct8; only over aligned in-bounds regions of still-all-Dct8 cells.
        bool TryLargeSquare(int bx, int by, int side, TransformType t, int margin)
        {
            for (int dy = 0; dy < side; dy++)
            {
                for (int dx = 0; dx < side; dx++)
                {
                    if (sizeAt[((by + dy) * bw) + bx + dx] != (int)TransformType.Dct8)
                    {
                        return false;
                    }
                }
            }

            int hf = adaptiveQuant ? hfMulBlock[(by * bw) + bx] : (int)blockHfMul;
            int costBig = Count(bx, by, t, hf) + perBlockPenalty;
            int costSplit = side * side * perBlockPenalty;
            for (int dy = 0; dy < side; dy++)
            {
                for (int dx = 0; dx < side; dx++)
                {
                    costSplit += Count(bx + dx, by + dy, TransformType.Dct8, hf);
                }
            }

            if (costBig + margin >= costSplit)
            {
                return false;
            }

            sizeAt[(by * bw) + bx] = (int)t;
            for (int dy = 0; dy < side; dy++)
            {
                for (int dx = 0; dx < side; dx++)
                {
                    if (dx != 0 || dy != 0)
                    {
                        sizeAt[((by + dy) * bw) + bx + dx] = -1;
                    }
                }
            }

            return true;
        }

        // Pass 0: Dct64 over aligned 8x8-block (64x64px) regions (very large flat areas only).
        for (int by = 0; by + 7 < bh; by += 8)
        {
            for (int bx = 0; bx + 7 < bw; bx += 8)
            {
                TryLargeSquare(bx, by, 8, TransformType.Dct64, 24);
            }
        }

        // Pass 1: Dct32 over aligned 4x4-block (32x32px) regions (skips cells already taken by a Dct64).
        for (int by = 0; by + 3 < bh; by += 4)
        {
            for (int bx = 0; bx + 3 < bw; bx += 4)
            {
                TryLargeSquare(bx, by, 4, TransformType.Dct32, dct32Margin);
            }
        }

        // Pass 2: the 16x16 (2x2-block) region choice, skipping any region already consumed by a larger block.
        for (int by = 0; by + 1 < bh; by += 2)
        {
            for (int bx = 0; bx + 1 < bw; bx += 2)
            {
                int tl = (by * bw) + bx;
                if (sizeAt[tl] != (int)TransformType.Dct8 || sizeAt[tl + 1] != (int)TransformType.Dct8 ||
                    sizeAt[((by + 1) * bw) + bx] != (int)TransformType.Dct8 || sizeAt[((by + 1) * bw) + bx + 1] != (int)TransformType.Dct8)
                {
                    continue; // part of a Dct32 block
                }

                int bHfMul = adaptiveQuant ? hfMulBlock[tl] : (int)blockHfMul;
                int Cnt(int x, int y, TransformType t) => Count(x, y, t, bHfMul);

                // Four ways to cover the 16x16 region; cost = luma AC non-zeros + a small per-block overhead.
                int cost8 = Cnt(bx, by, TransformType.Dct8) + Cnt(bx + 1, by, TransformType.Dct8)
                    + Cnt(bx, by + 1, TransformType.Dct8) + Cnt(bx + 1, by + 1, TransformType.Dct8) + (4 * perBlockPenalty);
                int cost16 = Cnt(bx, by, TransformType.Dct16) + perBlockPenalty;
                // Rectangular is only worth its extra block over Dct16, so require it to clearly beat both
                // square options (its non-square DCT is a worse fit unless the region is truly directional).
                int costV = Cnt(bx, by, TransformType.Dct16x8) + Cnt(bx + 1, by, TransformType.Dct16x8) + (2 * perBlockPenalty) + rectMargin; // two 8x16 columns
                int costH = Cnt(bx, by, TransformType.Dct8x16) + Cnt(bx, by + 1, TransformType.Dct8x16) + (2 * perBlockPenalty) + rectMargin; // two 16x8 rows

                int best = Math.Min(Math.Min(cost8, cost16), Math.Min(costV, costH));
                if (best >= cost8)
                {
                    continue; // Dct8 (4x) — leave as initialised
                }

                if (best == cost16)
                {
                    sizeAt[tl] = (int)TransformType.Dct16;
                    sizeAt[tl + 1] = sizeAt[((by + 1) * bw) + bx] = sizeAt[((by + 1) * bw) + bx + 1] = -1;
                }
                else if (best == costV)
                {
                    // Dct16x8 covers (1 wide x 2 tall) positions: two vertical strips.
                    sizeAt[tl] = (int)TransformType.Dct16x8;
                    sizeAt[((by + 1) * bw) + bx] = -1;
                    sizeAt[tl + 1] = (int)TransformType.Dct16x8;
                    sizeAt[((by + 1) * bw) + bx + 1] = -1;
                }
                else
                {
                    // Dct8x16 covers (2 wide x 1 tall) positions: two horizontal strips.
                    sizeAt[tl] = (int)TransformType.Dct8x16;
                    sizeAt[tl + 1] = -1;
                    sizeAt[((by + 1) * bw) + bx] = (int)TransformType.Dct8x16;
                    sizeAt[((by + 1) * bw) + bx + 1] = -1;
                }
            }
        }

        return sizeAt;
    }

    // Rough entropy-coded bit cost of one block's quantised luma (Y) AC coefficients, for RD block-size
    // choice: each non-zero coefficient costs ~ 2 + 2*log2(|q|+1) bits (sign + magnitude), which — unlike a
    // raw non-zero count — reflects that larger coefficients are dearer, so it stops over-picking the larger
    // transforms. The caller adds a fixed per-block overhead.
    private static int CountLumaAcNonzeros(float[] y, int stride, int bx, int by, TransformType t, DequantMatrixSet dm, uint globalScale, int bHfMul, VarDctFrameParams fp)
    {
        var (dw, dh) = JxlDct.DctSelectSize(t);
        int pw = dw * 8, ph = dh * 8;
        int numBlocks = dw * dh;
        float hfMul = 65536.0f / (globalScale * (float)bHfMul);
        float[] mat = dm.GetTransposed(1, t);
        bool needTr = JxlDct.NeedTranspose(t);
        (ushort X, ushort Y)[] order = JxlVarDctTables.NaturalOrder(JxlDct.OrderId(t));

        var buf = new float[pw * ph];
        var g = new JxlDct.Grid(buf, 0, pw, pw, ph);
        for (int yy = 0; yy < ph; yy++)
        {
            for (int xx = 0; xx < pw; xx++)
            {
                g.Set(xx, yy, y[(((by * 8) + yy) * stride) + (bx * 8) + xx]);
            }
        }

        JxlDct.Dct2D(g, false);
        int cnt = 0;
        for (int oi = numBlocks; oi < order.Length; oi++)
        {
            int k = needTr ? (order[oi].X * pw) + order[oi].Y : (order[oi].Y * pw) + order[oi].X;
            if (QuantAc(buf[k], mat[k] * hfMul, fp.QuantBias[1], fp.QuantBiasNumerator) != 0)
            {
                cnt++;
            }
        }

        return cnt;
    }

    // Forward AC quantiser: q = round(coeff / step). step = matrix[k] * hf_mul.
    private static int QuantAc(float coeff, float step, float quantBias, float quantBiasNum)
    {
        _ = quantBias;
        _ = quantBiasNum;
        return (int)MathF.Round(coeff / step);
    }

    // Dequantises exactly as JxlVarDct.DequantHf (quant bias then * step), so B's CfL pre-subtraction of
    // the reconstructed Y coefficient is bit-consistent with the decoder.
    private static float DequantAc(int q, float step, float quantBias, float quantBiasNum)
    {
        float qf = q;
        if (MathF.Abs(qf) <= 1.0f)
        {
            qf *= quantBias;
        }
        else
        {
            qf -= quantBiasNum / qf;
        }

        return qf * step;
    }

    private const int AnsTabSizeConst = 1 << 12;

    // --- a minimal sub-modular image writer (inverse of JxlModular.DecodeSubModular) ---
    private readonly record struct SubChannel(int[] Px, int W, int H);
    private readonly record struct ModToken(int Sym, uint Bits, int N);

    // Computes ClampedGradient-predictor residual tokens for a list of channels in decode order (each
    // channel raster-scanned), replicating JxlModular's single-leaf DecodeChannel neighbour rules exactly.
    private static List<ModToken> GradientTokens(List<SubChannel> chans)
    {
        var outTokens = new List<ModToken>();
        foreach (SubChannel ch in chans)
        {
            int w = ch.W, h = ch.H;
            int[] px = ch.Px;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    long left = x > 0 ? px[(y * w) + x - 1] : (y > 0 ? px[((y - 1) * w) + x] : 0);
                    long top = y > 0 ? px[((y - 1) * w) + x] : left;
                    long topleft = (x > 0 && y > 0) ? px[((y - 1) * w) + x - 1] : left;
                    long guess = ClampedGradient(left, top, topleft);
                    int packed = PackSigned((int)(px[(y * w) + x] - guess));
                    (int token, int nbits, int bits) = PackHybridFull(LitSplit, LitMsb, LitLsb, packed);
                    outTokens.Add(new ModToken(token, (uint)bits, nbits));
                }
            }
        }

        return outTokens;
    }

    // Writes the single-context ANS histograms header for the frame's global modular code (numContexts
    // == 1 for a single-leaf tree, so no context map). Shared by every LfGroup sub-image.
    private static void WriteSingleContextAnsHeader(JxlBitWriter body, int[] normalized, int logAlpha)
    {
        body.WriteBool(false);                    // lz77 disabled
        body.WriteBool(false);                    // use_prefix_code = false (ANS)
        body.WriteBits((uint)(logAlpha - 5), 2);
        WriteUintConfig(body, LitSplit, LitMsb, LitLsb, logAlpha);
        JxlEntropy.WriteHistogram(body, normalized, JxlEntropy.HistShift);
    }

    private const float DcNodeThreshold = 96f; // MA-tree split threshold for the DC/metadata learner

    // Writes one modular sub-image that references the global learned tree + clustered code
    // (use_global_tree = true): the GroupHeader then the stream's ANS ops via the lossless emitter.
    private static void WriteSubModularOps(JxlBitWriter body, List<Op> ops, int[] ctxs, PixelPlanCtx plan, JxlAnsWriter ans, int wpMode)
    {
        body.WriteBool(true);            // use_global_tree = true
        WriteWpHeaderBits(body, wpMode);
        body.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0
        EmitOpsAns(body, ops, ctxs, plan, ans);
    }

    // Writes one modular sub-image that references the global tree + code (use_global_tree = true): the
    // GroupHeader then a self-contained ANS stream over the channels' residual tokens.
    private static void WriteSubModularUsingGlobal(JxlBitWriter body, List<ModToken> tokens, int[] normalized, int logAlpha)
    {
        body.WriteBool(true);            // use_global_tree = true
        body.WriteBits(1, 1);            // WpHeader all_default
        body.WriteU32(0, E.Val(0), E.Val(1), E.BitsOff(4, 2), E.BitsOff(8, 18)); // num_transforms = 0

        var ans = new JxlAnsWriter(new[] { normalized }, logAlpha);
        var ansTokens = new List<AnsToken>(tokens.Count);
        foreach (ModToken t in tokens)
        {
            ansTokens.Add(new AnsToken(0, t.Sym, t.Bits, t.N));
        }

        ans.Encode(body, ansTokens);
    }

    private static long ClampedGradient(long a, long b, long c)
    {
        long grad = a + b - c;
        long lo = Math.Min(a, b);
        long hi = Math.Max(a, b);
        return grad < lo ? lo : (grad > hi ? hi : grad);
    }

    private static int NextPow2(int v)
    {
        int p = 1;
        while (p < v)
        {
            p <<= 1;
        }

        return p;
    }

    private static int BitLength(int v) => v <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(v - 1));
}
