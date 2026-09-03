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
    public static byte[] EncodeVarDct(ImageFrame image, uint globalScale = 4096, uint quantLf = 32, uint blockHfMul = 1, int[]? passShifts = null)
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
        List<byte[]> sections = BuildVarDctSections(srgb, w, h, globalScale, quantLf, blockHfMul, shifts);
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
        return EncodeVarDct(image, gs, qlf, hfm, passShifts);
    }

    // Maps a Butteraugli-style distance to (global_scale, quant_lf, block_hf_mul). global_scale is fixed;
    // block_hf_mul scales AC precision (~1/distance) and quant_lf scales DC precision, capped so the DC
    // integers stay inside the 16-bit modular buffer.
    private static (uint GlobalScale, uint QuantLf, uint BlockHfMul) QuantForDistance(float distance)
    {
        float d = Math.Clamp(distance, 0.1f, 25f);
        uint hfm = (uint)Math.Clamp((int)MathF.Round(64f / d), 1, 4096);
        uint qlf = (uint)Math.Clamp((int)MathF.Round(256f / d), 1, 512);
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
    private static List<byte[]> BuildVarDctSections(float[][] srgb, int w, int h, uint globalScale, uint quantLf, uint blockHfMul, int[] shifts)
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
        // Dct8 needs transposition (NeedTranspose(Dct8) == true, since its 1x1 block has h >= w): the
        // decoder places coefficients at the transposed block position and dequantises with the transposed
        // matrix, so the encoder must do the same.
        float[] matX = dm.GetTransposed(0, TransformType.Dct8);
        float[] matY = dm.GetTransposed(1, TransformType.Dct8);
        float[] matB = dm.GetTransposed(2, TransformType.Dct8);
        float hfMul = 65536.0f / (globalScale * (float)blockHfMul);
        (ushort X, ushort Y)[] order = JxlVarDctTables.NaturalOrder(0); // Dct8 scan order

        var dcY = new int[nbData];
        var dcX = new int[nbData];
        var dcB = new int[nbData];
        // AC coefficients per block, laid out by scan position: ac[c][block*64 + oi]. oi 0 (DC) unused.
        var ac = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            ac[c] = new int[nbData * 64];
        }

        var coeffRaster = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            coeffRaster[c] = new float[64];
        }

        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int bi = (by * bw) + bx;
                for (int c = 0; c < 3; c++)
                {
                    var g = new JxlDct.Grid(coeffRaster[c], 0, 8, 8, 8);
                    for (int yy = 0; yy < 8; yy++)
                    {
                        for (int xx = 0; xx < 8; xx++)
                        {
                            g.Set(xx, yy, xyb[c][(((by * 8) + yy) * stride) + (bx * 8) + xx]);
                        }
                    }

                    JxlDct.Dct2D(g, false); // forward DCT -> coeffRaster[c] in raster order
                }

                // DC (channel order X=0, Y=1, B=2); B pre-subtracts the dequantised Y DC.
                int yDcInt = (int)MathF.Round(coeffRaster[1][0] / scaleY);
                dcY[bi] = yDcInt;
                dcX[bi] = (int)MathF.Round(coeffRaster[0][0] / scaleX);
                dcB[bi] = (int)MathF.Round((coeffRaster[2][0] - (yDcInt * scaleY)) / scaleB);

                // AC: quantise each channel; B pre-subtracts the dequantised Y AC per coefficient.
                for (int oi = 1; oi < 64; oi++)
                {
                    // Transposed placement raster (matches the decoder's NeedTranspose swap for Dct8).
                    int k = (order[oi].X * 8) + order[oi].Y;
                    // X and Y quantise directly.
                    int qX = QuantAc(coeffRaster[0][k], matX[k] * hfMul, fp.QuantBias[0], fp.QuantBiasNumerator);
                    int qY = QuantAc(coeffRaster[1][k], matY[k] * hfMul, fp.QuantBias[1], fp.QuantBiasNumerator);
                    ac[0][(bi * 64) + oi] = qX;
                    ac[1][(bi * 64) + oi] = qY;
                    // B: subtract the dequantised Y coefficient (CfL kb == 1) before quantising.
                    float yDeq = DequantAc(qY, matY[k] * hfMul, fp.QuantBias[1], fp.QuantBiasNumerator);
                    ac[2][(bi * 64) + oi] = QuantAc(coeffRaster[2][k] - yDeq, matB[k] * hfMul, fp.QuantBias[2], fp.QuantBiasNumerator);
                }
            }
        }

        // Build the LfGroup modular sub-images. libjxl (and jxl-oxide) require a *global* MA tree in
        // LfGlobal that every sub-image references (use_global_tree = true); local per-image trees are
        // rejected. We use a single-leaf ClampedGradient tree and one shared histogram over all channels
        // of both sub-images.
        var dcImage = new List<SubChannel> { new(dcY, bw, bh), new(dcX, bw, bh), new(dcB, bw, bh) };
        int cfW = (w + 63) / 64, cfH = (h + 63) / 64;
        var blockInfo = new int[nbData * 2];       // row0 = dct_select (0 = Dct8), row1 = hf_mul - 1
        for (int i = 0; i < nbData; i++)
        {
            blockInfo[i] = 0;
            blockInfo[nbData + i] = (int)blockHfMul - 1;
        }

        var hfMeta = new List<SubChannel>
        {
            new(new int[cfW * cfH], cfW, cfH),     // x_from_y (all 0)
            new(new int[cfW * cfH], cfW, cfH),     // b_from_y (all 0)
            new(blockInfo, nbData, 2),             // block info
            new(new int[bw * bh], bw, bh),         // sharpness (all 0)
        };

        List<ModToken> dcTokens = GradientTokens(dcImage);
        List<ModToken> metaTokens = GradientTokens(hfMeta);
        var modHist = new long[1];
        AccumulateHist(dcTokens, ref modHist);
        AccumulateHist(metaTokens, ref modHist);
        int modLogAlpha = Math.Max(5, JxlBits.CeilLog2(modHist.Length));
        int[] modNorm = JxlEntropy.NormalizeCounts(modHist, JxlEntropy.HistShift);

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
        int numPasses = shifts.Length;
        var passGroupTokens = new List<ModToken>[numPasses][]; // [pass][group]
        var passNorm = new int[numPasses][];
        var passLogAlpha = new int[numPasses];
        var remaining = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            remaining[c] = (int[])ac[c].Clone();
        }

        for (int p = 0; p < numPasses; p++)
        {
            int sp = shifts[p];
            var qp = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                qp[c] = new int[nbData * 64];
                for (int idx = 0; idx < nbData * 64; idx++)
                {
                    int q = remaining[c][idx] >> sp; // arithmetic shift (floor) — matches decoder's << sp
                    qp[c][idx] = q;
                    remaining[c][idx] -= q << sp;
                }
            }

            passGroupTokens[p] = new List<ModToken>[numGroups];
            var ph = new long[1];
            for (int g = 0; g < numGroups; g++)
            {
                int gx = g % groupsPerRow, gy = g / groupsPerRow;
                int bx0 = gx * groupBlocks, by0 = gy * groupBlocks;
                int gBw = Math.Min(groupBlocks, bw - bx0);
                int gBh = Math.Min(groupBlocks, bh - by0);
                passGroupTokens[p][g] = BuildHfTokens(bx0, by0, gBw, gBh, bw, qp);
                AccumulateHist(passGroupTokens[p][g], ref ph);
            }

            passLogAlpha[p] = Math.Max(5, JxlBits.CeilLog2(ph.Length));
            passNorm[p] = JxlEntropy.NormalizeCounts(ph, JxlEntropy.HistShift);
        }

        var leaf = new LearnedTree(new MaTreeNode { Property = -1, Predictor = 5 }); // ClampedGradient
        int nbBits = BitLength(NextPow2(bw * bh));

        void WriteLfGlobal(JxlBitWriter b)
        {
            b.WriteBool(true);  // lf_channel_dequant all_default {1/32, 1/4, 1/2}
            b.WriteU32(globalScale, E.BitsOff(11, 1), E.BitsOff(11, 2049), E.BitsOff(12, 4097), E.BitsOff(16, 8193));
            b.WriteU32(quantLf, E.Val(16), E.BitsOff(5, 1), E.BitsOff(8, 1), E.BitsOff(16, 1));
            b.WriteBool(true);  // hf_block_context all_default (15 clusters, default map)
            b.WriteBool(true);  // lf_channel_correlation all_default (base_x 0, base_b 1)
            b.WriteBits(1, 1);  // GlobalModular: has_tree = 1 (shared single-leaf tree for all sub-images)
            WriteTree(b, leaf.Tokens);
            WriteSingleContextAnsHeader(b, modNorm, modLogAlpha);
        }

        void WriteLfGroup(JxlBitWriter b)
        {
            b.WriteBits(0, 2);  // extra_precision = 0
            WriteSubModularUsingGlobal(b, dcTokens, modNorm, modLogAlpha);
            b.WriteBits((uint)(nbData - 1), nbBits); // HfMetadata num_blocks
            WriteSubModularUsingGlobal(b, metaTokens, modNorm, modLogAlpha);
        }

        void WriteHfGlobal(JxlBitWriter b)
        {
            b.WriteBool(true);  // DequantMatrixSet all_default
            b.WriteBits(0, presetBits); // num_hf_presets - 1 == 0 (one preset)
            for (int p = 0; p < numPasses; p++)
            {
                WriteHfPass(b, passNorm[p], passLogAlpha[p]);
            }
        }

        void WritePassGroup(JxlBitWriter b, int p, int g)
        {
            // hf_preset selector width = BitLength(NextPow2(num_hf_presets)); num_hf_presets == 1 => 0 bits.
            var ans = new JxlAnsWriter(new[] { passNorm[p] }, passLogAlpha[p]);
            var toks = new List<AnsToken>(passGroupTokens[p][g].Count);
            foreach (ModToken t in passGroupTokens[p][g])
            {
                toks.Add(new AnsToken(0, t.Sym, t.Bits, t.N));
            }

            ans.Encode(b, toks);
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

    // The single HF pass: used_orders = 0 (natural coefficient order, no permutations), then the HfDist
    // entropy code. All 495*num_block_clusters contexts map to one shared histogram (context clustering is
    // an optimisation left for later; correctness only needs the histogram to cover every emitted symbol).
    private static void WriteHfPass(JxlBitWriter body, int[] hfNormalized, int hfLogAlpha)
    {
        body.WriteU32(0, E.Val(0x5F), E.Val(0x13), E.Val(0x00), E.BitsOff(13, 0)); // used_orders = 0
        int numContexts = 495 * DefaultNumBlockClusters; // num_hf_presets == 1
        body.WriteBool(false);   // lz77 disabled
        body.WriteBool(true);    // context map: is_simple
        body.WriteBits(0, 2);    // bits_per_entry = 0 => all contexts map to cluster 0
        body.WriteBool(false);   // use_prefix_code = false (ANS)
        body.WriteBits((uint)(hfLogAlpha - 5), 2);
        WriteUintConfig(body, LitSplit, LitMsb, LitLsb, hfLogAlpha);
        JxlEntropy.WriteHistogram(body, hfNormalized, JxlEntropy.HistShift);
        _ = numContexts;
    }

    private const int DefaultNumBlockClusters = 15;

    // Builds the PassGroup HF coefficient tokens for one 256px group's block window (inverse of
    // JxlVarDct.WriteHfCoeff), all in cluster 0. Emission order matches the decoder: group-local block
    // raster (y, x), channel order {Y, X, B}; per block+channel a non-zeros count token, then coefficient
    // tokens in scan order up to (and including) the last non-zero. bx0/by0 is the group origin in blocks,
    // gBw/gBh the group block size, bw the full-image block width (to index ac).
    private static List<ModToken> BuildHfTokens(int bx0, int by0, int gBw, int gBh, int bw, int[][] ac)
    {
        var tokens = new List<ModToken>();
        for (int y = 0; y < gBh; y++)
        {
            for (int x = 0; x < gBw; x++)
            {
                int bi = ((by0 + y) * bw) + bx0 + x;
                for (int cc = 0; cc < 3; cc++)
                {
                    int c = new[] { 1, 0, 2 }[cc]; // Y, X, B
                    int baseIdx = bi * 64;
                    int nonZeros = 0, lastNz = 0;
                    for (int oi = 1; oi < 64; oi++)
                    {
                        if (ac[c][baseIdx + oi] != 0)
                        {
                            nonZeros++;
                            lastNz = oi;
                        }
                    }

                    tokens.Add(HybridToken(nonZeros)); // non-zeros count (used directly by the decoder)
                    for (int oi = 1; oi <= lastNz; oi++)
                    {
                        tokens.Add(HybridToken(PackSigned(ac[c][baseIdx + oi])));
                    }
                }
            }
        }

        return tokens;
    }

    private static ModToken HybridToken(int value)
    {
        (int token, int nbits, int bits) = PackHybridFull(LitSplit, LitMsb, LitLsb, value);
        return new ModToken(token, (uint)bits, nbits);
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
