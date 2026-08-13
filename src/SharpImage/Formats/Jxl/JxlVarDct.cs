// JPEG XL VarDCT (lossy) frame decoder: LfGlobal (quantizer, HF block context, chroma correlation,
// global Modular), per-LF-group DC image + block metadata, HfGlobal (dequant matrices + HF passes),
// per-group AC coefficient decode, then reconstruction (LF dequant, adaptive LF smoothing,
// chroma-from-luma, per-varblock dequant + inverse transform, XYB -> sRGB). Ported from jxl-oxide
// (jxl-vardct, jxl-render vardct) and libjxl. Loop filters (Gaborish/EPF) are applied when present.
using System;
using System.Collections.Generic;

namespace SharpImage.Formats.Jxl;

internal sealed class VarDctFrameParams
{
    public int Width, Height, BitDepth;
    public int GroupDim, NumGroups, NumLf, GroupsPerRow, NumPasses;
    public bool Xyb;
    public ulong Flags;
    public int XQmScale, BQmScale;
    // opsin inverse matrix + biases (defaults; assume all_default metadata for now).
    public float[] OpsinInv = { 11.031566901960783f, -9.866943921568629f, -0.16462299647058826f, -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f, -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f };
    public float[] OpsinBias = { -0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f };
    public float[] QuantBias = { 1.0f - 0.05465007330715401f, 1.0f - 0.07005449891748593f, 1.0f - 0.049935103337343655f };
    public float QuantBiasNumerator = 0.145f;
    public float IntensityTarget = 255.0f;
    public bool SkipAdaptiveLfSmoothing;

    // Loop filters (defaults per JXL spec; apply for all_default frames).
    public bool GabEnabled = true;
    public float[][] GabWeights = { new[] { 0.115169525f, 0.061248592f }, new[] { 0.115169525f, 0.061248592f }, new[] { 0.115169525f, 0.061248592f } };
    public int EpfIters = 2;
    public float[] EpfChannelScale = { 40.0f, 5.0f, 3.5f };
    public float[] EpfSharpLut = { 0f, 1f / 7f, 2f / 7f, 3f / 7f, 4f / 7f, 5f / 7f, 6f / 7f, 1f };
    public float EpfQuantMul = 0.46f;
    public float EpfPass0SigmaScale = 0.9f;
    public float EpfPass2SigmaScale = 6.5f;
    public float EpfBorderSadMul = 2.0f / 3.0f;
}

internal struct BlockInfo
{
    public bool Occupied;
    public bool IsData;
    public TransformType Dct;
    public int HfMul;
}

internal sealed class HfBlockContext
{
    public uint[] QfThresholds = Array.Empty<uint>();
    public int[][] LfThresholds = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
    public byte[] BlockCtxMap = Array.Empty<byte>();
    public int NumBlockClusters;
}

internal sealed class HfPass
{
    // permutation[orderId][channel] = coefficient order (x,y) coords, or null to use natural order.
    public (ushort X, ushort Y)[]?[][] Permutation = new (ushort, ushort)[13][][];
    public JxlAnsCode HfDist = null!;
}

internal sealed class LfGroupData
{
    public int[][] LfQuant = null!;            // [3][ (w/8)*(h/8) ] DC quant values
    public int LfW, LfH;                       // in DC blocks
    public BlockInfo[] BlockInfoGrid = null!;  // bw*bh
    public int Bw, Bh;                          // in 8x8 blocks
    public int ExtraPrecision;
    public int[] XFromY = null!;                // 64x64 CfL grid (X channel)
    public int[] BFromY = null!;
    public int XFromYW, XFromYH;
    public float[] EpfSigma = null!;            // bw*bh per-block EPF sigma
}

internal static class JxlVarDct
{
    // --- LfGlobal VarDct structures ---
    private sealed class Quantizer { public uint GlobalScale; public uint QuantLf; }

    private sealed class LfChannelCorrelation
    {
        public uint ColourFactor = 84;
        public float BaseCorrelationX;
        public float BaseCorrelationB = 1.0f;
        public int XFactorLf = 128;
        public int BFactorLf = 128;
    }

    private static float[] ReadLfChannelDequant(JxlBitReader br)
    {
        if (br.ReadBool())
        {
            return new[] { 1f / 32f, 1f / 4f, 1f / 2f };
        }

        return new[] { br.ReadF16(), br.ReadF16(), br.ReadF16() };
    }

    private static Quantizer ReadQuantizer(JxlBitReader br)
    {
        var q = new Quantizer
        {
            GlobalScale = JxlBits.ReadU32(br, JxlBitReader.U32Enc.BitsOff(11, 1), JxlBitReader.U32Enc.BitsOff(11, 2049), JxlBitReader.U32Enc.BitsOff(12, 4097), JxlBitReader.U32Enc.BitsOff(16, 8193)),
            QuantLf = JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(16), JxlBitReader.U32Enc.BitsOff(5, 1), JxlBitReader.U32Enc.BitsOff(8, 1), JxlBitReader.U32Enc.BitsOff(16, 1)),
        };
        return q;
    }

    private static LfChannelCorrelation ReadLfChannelCorrelation(JxlBitReader br)
    {
        var c = new LfChannelCorrelation();
        if (br.ReadBool())
        {
            return c;
        }

        c.ColourFactor = JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(84), JxlBitReader.U32Enc.Val(256), JxlBitReader.U32Enc.BitsOff(8, 2), JxlBitReader.U32Enc.BitsOff(16, 258));
        c.BaseCorrelationX = br.ReadF16();
        c.BaseCorrelationB = br.ReadF16();
        c.XFactorLf = (int)br.ReadBits(8);
        c.BFactorLf = (int)br.ReadBits(8);
        return c;
    }

    private static readonly byte[] DefaultBlockCtxMap =
    {
        0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14,
        14, 14, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14,
    };

    private static HfBlockContext ReadHfBlockContext(JxlBitReader br)
    {
        var ctx = new HfBlockContext();
        if (br.ReadBool())
        {
            ctx.NumBlockClusters = 15;
            ctx.BlockCtxMap = (byte[])DefaultBlockCtxMap.Clone();
            return ctx;
        }

        int bsize = 1;
        for (int i = 0; i < 3; i++)
        {
            int numLfThr = (int)br.ReadBits(4);
            bsize *= numLfThr + 1;
            var thr = new int[numLfThr];
            for (int j = 0; j < numLfThr; j++)
            {
                uint t = JxlBits.ReadU32(br, JxlBitReader.U32Enc.BitsOff(4, 0), JxlBitReader.U32Enc.BitsOff(8, 16), JxlBitReader.U32Enc.BitsOff(16, 272), JxlBitReader.U32Enc.BitsOff(32, 65808));
                thr[j] = JxlBits.UnpackSigned(t);
            }

            ctx.LfThresholds[i] = thr;
        }

        int numQfThr = (int)br.ReadBits(4);
        bsize *= numQfThr + 1;
        var qf = new uint[numQfThr];
        for (int j = 0; j < numQfThr; j++)
        {
            uint t = JxlBits.ReadU32(br, JxlBitReader.U32Enc.BitsOff(2, 0), JxlBitReader.U32Enc.BitsOff(3, 4), JxlBitReader.U32Enc.BitsOff(5, 12), JxlBitReader.U32Enc.BitsOff(8, 44));
            qf[j] = 1 + t;
        }

        ctx.QfThresholds = qf;
        ctx.BlockCtxMap = JxlEntropy.ReadClusters(bsize * 39, br, out int numClusters);
        ctx.NumBlockClusters = numClusters;
        return ctx;
    }

    private static readonly int[][] BlockSizeList =
    {
        new[] { 8, 8 }, new[] { 8, 8 }, new[] { 16, 16 }, new[] { 32, 32 }, new[] { 16, 8 }, new[] { 32, 8 },
        new[] { 32, 16 }, new[] { 64, 64 }, new[] { 64, 32 }, new[] { 128, 128 }, new[] { 128, 64 }, new[] { 256, 256 }, new[] { 256, 128 },
    };

    private static HfPass ReadHfPass(JxlBitReader br, HfBlockContext hfCtx, int numHfPresets)
    {
        var pass = new HfPass();
        uint usedOrders = JxlBits.ReadU32(br, JxlBitReader.U32Enc.Val(0x5F), JxlBitReader.U32Enc.Val(0x13), JxlBitReader.U32Enc.Val(0x00), JxlBitReader.U32Enc.BitsOff(13, 0));
        JxlAnsCode? orderCode = null;
        JxlAnsReader? orderRd = null;
        if (usedOrders != 0)
        {
            orderCode = JxlEntropy.DecodeHistograms(8, br);
            orderRd = new JxlAnsReader(orderCode, br);
        }

        for (int idx = 0; idx < 13; idx++)
        {
            pass.Permutation[idx] = new (ushort, ushort)[3][];
            if (orderRd != null && (usedOrders & 1) != 0)
            {
                int bw = BlockSizeList[idx][0], bh = BlockSizeList[idx][1];
                int size = bw * bh;
                int skip = size / 64;
                for (int c = 0; c < 3; c++)
                {
                    int[] perm = JxlEntropy.ReadPermutation(br, orderRd, size, skip);
                    var nat = JxlVarDctTables.NaturalOrder(idx);
                    var ord = new (ushort, ushort)[perm.Length];
                    for (int k = 0; k < perm.Length; k++)
                    {
                        ord[k] = nat[perm[k]];
                    }

                    pass.Permutation[idx][c] = ord;
                }
            }

            usedOrders >>= 1;
        }

        orderRd?.CheckFinal();
        pass.HfDist = JxlEntropy.DecodeHistograms((int)(495 * numHfPresets * hfCtx.NumBlockClusters), br);
        return pass;
    }

    private static (ushort X, ushort Y)[] PassOrder(HfPass pass, int orderId, int channel)
    {
        var p = pass.Permutation[orderId][channel];
        return p ?? JxlVarDctTables.NaturalOrder(orderId);
    }

    // --- LfGroup (DC image + block metadata) ---
    private static LfGroupData ReadLfGroup(JxlBitReader br, VarDctFrameParams fp, int lfGroupIdx, int lfW, int lfH, List<MaNode>? gTree, JxlAnsCode? gCode, Quantizer quant, float[] lfDequant, HfBlockContext hfCtx)
    {
        var lg = new LfGroupData();
        int w8 = (lfW + 7) / 8;
        int h8 = (lfH + 7) / 8;

        // LfCoeff: extra_precision (2 bits) + 3-channel modular DC image (w8 x h8), stream 1+lf_group_idx.
        lg.ExtraPrecision = (int)br.ReadBits(2);
        var lfChans = new List<JxlChannel> { new(w8, h8), new(w8, h8), new(w8, h8) };
        JxlModular.DecodeSubModular(br, lfChans, gTree, gCode, 1 + lfGroupIdx, fp.BitDepth);
        lg.LfQuant = new[] { lfChans[0].Px, lfChans[1].Px, lfChans[2].Px };
        lg.LfW = w8;
        lg.LfH = h8;

        // HfMetadata: 4-channel modular (x_from_y, b_from_y, blockinfo(dct,hfmul), sharpness).
        int bw = w8;
        int bh = h8;
        int nbBlocks = 1 + (int)br.ReadBits(BitLength((uint)NextPow2(bw * bh)));
        var metaChans = new List<JxlChannel>
        {
            new((lfW + 63) / 64, (lfH + 63) / 64), // x_from_y
            new((lfW + 63) / 64, (lfH + 63) / 64), // b_from_y
            new(nbBlocks, 2),                       // block info: [0]=dct_select, [1]=hf_mul-1
            new(bw, bh),                            // sharpness
        };
        JxlModular.DecodeSubModular(br, metaChans, gTree, gCode, 1 + (2 * fp.NumLf) + lfGroupIdx, fp.BitDepth);
        lg.XFromYW = metaChans[0].W; lg.XFromYH = metaChans[0].H; lg.XFromY = metaChans[0].Px; lg.BFromY = metaChans[1].Px;

        int[] blockInfoRaw0 = metaChans[2].Px;
        int biW = metaChans[2].W;

        lg.Bw = bw;
        lg.Bh = bh;
        lg.BlockInfoGrid = new BlockInfo[bw * bh];
        lg.EpfSigma = new float[bw * bh];
        int[] sharpness = metaChans[3].Px;
        // epf_quant_mul = quant_mul * 65536 / global_scale (per-varblock divided by hf_mul, x sharp_lut).
        float epfQuantMul = fp.EpfQuantMul * 65536f / quant.GlobalScale;
        int dataIdx = 0;
        int y = 0;
        while (y < bh)
        {
            int x = 0;
            while (x < bw)
            {
                if (!lg.BlockInfoGrid[(y * bw) + x].Occupied)
                {
                    var dctSelect = (TransformType)blockInfoRaw0[dataIdx];
                    int mul = metaChans[2].Px[biW + dataIdx]; // row 1
                    int hfMul = mul + 1;
                    var (dw, dh) = JxlDct.DctSelectSize(dctSelect);
                    for (int dy = 0; dy < dh; dy++)
                    {
                        for (int dx = 0; dx < dw; dx++)
                        {
                            ref BlockInfo bi = ref lg.BlockInfoGrid[((y + dy) * bw) + x + dx];
                            bi.Occupied = true;
                            if (dx == 0 && dy == 0)
                            {
                                bi.IsData = true;
                                bi.Dct = dctSelect;
                                bi.HfMul = hfMul;
                            }

                            int sp = sharpness[((y + dy) * bw) + x + dx];
                            sp = Math.Clamp(sp, 0, 7);
                            lg.EpfSigma[((y + dy) * bw) + x + dx] = epfQuantMul / hfMul * fp.EpfSharpLut[sp];
                        }
                    }

                    dataIdx++;
                    x += dw;
                }
                else
                {
                    x++;
                }
            }

            y++;
        }

        return lg;
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

    private static int BitLength(uint v) => v <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v - 1);

    // extra fields on LfGroupData for CfL grids.
    // (declared here via partial extension pattern would be cleaner; store on the object)

    // --- HF coefficient decode (write_hf_coeff) ---
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

    private static void WriteHfCoeff(JxlBitReader br, VarDctFrameParams fp, HfBlockContext hfCtx, HfPass pass, LfGroupData lg, int numHfPresets, float[][] coeffOut, int coeffStride)
    {
        var code = pass.HfDist;
        int lfIdxMul = (hfCtx.LfThresholds[0].Length + 1) * (hfCtx.LfThresholds[1].Length + 1) * (hfCtx.LfThresholds[2].Length + 1);
        int hfIdxMul = hfCtx.QfThresholds.Length + 1;

        int hfpBits = BitLength((uint)NextPow2(fp.NumGroups));
        int hfp = (int)br.ReadBits(hfpBits);
        int ctxSize = 495 * hfCtx.NumBlockClusters;
        byte[] clusterMap = new byte[ctxSize];
        Array.Copy(code.ContextMap, ctxSize * hfp, clusterMap, 0, ctxSize);

        var rd = new JxlAnsReader(code, br);

        int width = lg.Bw, height = lg.Bh;
        uint[][] nonZerosGrid = { new uint[width], new uint[width], new uint[width] };

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ref BlockInfo bi = ref lg.BlockInfoGrid[(y * width) + x];
                if (!bi.IsData)
                {
                    continue;
                }

                TransformType dctSelect = bi.Dct;
                int qf = bi.HfMul;
                var (w8, h8) = JxlDct.DctSelectSize(dctSelect);
                int numBlocks = w8 * h8;
                int numBlocksLog = System.Numerics.BitOperations.TrailingZeroCount(numBlocks);
                int orderId = JxlDct.OrderId(dctSelect);

                int lfIdx = 0;
                foreach (int c in new[] { 0, 2, 1 })
                {
                    int[] thr = hfCtx.LfThresholds[c];
                    lfIdx *= thr.Length + 1;
                    int q = lg.LfQuant[c][(y * lg.LfW) + x];
                    foreach (int threshold in thr)
                    {
                        if (q > threshold)
                        {
                            lfIdx++;
                        }
                    }
                }

                int hfIdx = 0;
                foreach (uint threshold in hfCtx.QfThresholds)
                {
                    if (qf > threshold)
                    {
                        hfIdx++;
                    }
                }

                for (int cc = 0; cc < 3; cc++)
                {
                    int chIdx = (cc * 13) + orderId;
                    int c = new[] { 1, 0, 2 }[cc]; // y, x, b
                    int sx = x, sy = y;

                    int idx = ((chIdx * hfIdxMul) + hfIdx) * lfIdxMul + lfIdx;
                    int blockCtx = hfCtx.BlockCtxMap[idx];
                    uint predicted;
                    if (sy == 0)
                    {
                        predicted = sx == 0 ? 32 : nonZerosGrid[c][sx - 1];
                    }
                    else if (sx == 0)
                    {
                        predicted = nonZerosGrid[c][sx];
                    }
                    else
                    {
                        predicted = (nonZerosGrid[c][sx] + nonZerosGrid[c][sx - 1] + 1) >> 1;
                    }

                    uint nzIdx = predicted >= 8 ? 4 + (predicted / 2) : predicted;
                    int nonZerosCtx = (int)(blockCtx + (nzIdx * hfCtx.NumBlockClusters));

                    uint nonZeros = rd.ReadHybridUintClustered(clusterMap[nonZerosCtx]);
                    uint nonZerosVal = (uint)((nonZeros + numBlocks - 1) >> numBlocksLog);
                    for (int dx = 0; dx < w8; dx++)
                    {
                        nonZerosGrid[c][sx + dx] = nonZerosVal;
                    }

                    if (nonZeros == 0)
                    {
                        continue;
                    }

                    float[] coeffGrid = coeffOut[c];
                    uint isPrevNonzero = nonZeros <= numBlocks * 4 ? 1u : 0u;
                    var order = PassOrder(pass, orderId, c);
                    int coeffCtxBase = (int)((blockCtx * 458) + (37 * hfCtx.NumBlockClusters));

                    for (int oi = numBlocks; oi < order.Length; oi++)
                    {
                        int fidx = oi - numBlocks;
                        uint nzForCtx = (nonZeros - 1) >> numBlocksLog;
                        uint fq = (uint)(fidx >> numBlocksLog);
                        int coeffCtx = (int)(((CoeffNumNonzeroContext[nzForCtx] + CoeffFreqContext[fq]) * 2) + isPrevNonzero);
                        byte cluster = clusterMap[coeffCtxBase + coeffCtx];
                        uint ucoeff = rd.ReadHybridUintClustered(cluster);
                        if (ucoeff == 0)
                        {
                            isPrevNonzero = 0;
                            continue;
                        }

                        int coeff = JxlBits.UnpackSigned(ucoeff);
                        var (dx, dy) = order[oi];
                        int cx = dx, cy = dy;
                        if (JxlDct.NeedTranspose(dctSelect))
                        {
                            (cx, cy) = (cy, cx);
                        }

                        int px = (sx * 8) + cx;
                        int py = (sy * 8) + cy;
                        coeffGrid[(py * coeffStride) + px] += coeff;
                        isPrevNonzero = 1;
                        nonZeros--;
                        if (nonZeros == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        rd.CheckFinal();
    }

    // dequant HF: multiply decoded coefficients by dequant matrix + bias.
    private static void DequantHf(VarDctFrameParams fp, HfBlockContext hfCtx, DequantMatrixSet dm, Quantizer quant, LfGroupData lg, float[][] coeff, int coeffStride)
    {
        float[] qmScale = { MathF.Pow(0.8f, fp.XQmScale - 2), 1.0f, MathF.Pow(0.8f, fp.BQmScale - 2) };
        for (int channel = 0; channel < 3; channel++)
        {
            float quantBias = fp.QuantBias[channel];
            for (int by = 0; by < lg.Bh; by++)
            {
                for (int bx = 0; bx < lg.Bw; bx++)
                {
                    ref BlockInfo bi = ref lg.BlockInfoGrid[(by * lg.Bw) + bx];
                    if (!bi.IsData)
                    {
                        continue;
                    }

                    TransformType dctSelect = bi.Dct;
                    var (bw, bh) = JxlDct.DctSelectSize(dctSelect);
                    int width = bw * 8, height = bh * 8;
                    int left = bx * 8, top = by * 8;
                    bool needTr = JxlDct.NeedTranspose(dctSelect);
                    float mul = 65536.0f / (quant.GlobalScale * (float)bi.HfMul) * qmScale[channel];
                    float[] matrix = needTr ? dm.GetTransposed(channel, dctSelect) : dm.Get(channel, dctSelect);
                    float[] g = coeff[channel];
                    for (int yy = 0; yy < height; yy++)
                    {
                        for (int xx = 0; xx < width; xx++)
                        {
                            int gi = ((top + yy) * coeffStride) + left + xx;
                            float q = g[gi];
                            float m = matrix[(yy * width) + xx];
                            if (MathF.Abs(q) <= 1.0f)
                            {
                                q *= quantBias;
                            }
                            else
                            {
                                q -= fp.QuantBiasNumerator / q;
                            }

                            q *= m;
                            q *= mul;
                            g[gi] = q;
                        }
                    }
                }
            }
        }
    }

    // --- reconstruction helpers ---
    private static void CopyLfDequant(float[] outGrid, int stride, int w, int h, Quantizer quant, float mLf, int[] channel, int extraPrecision)
    {
        int precisionScale = 1 << (9 - extraPrecision);
        double scaleInv = (double)quant.GlobalScale * quant.QuantLf;
        float scale = (float)(mLf * precisionScale / scaleInv);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                outGrid[(y * stride) + x] = channel[(y * w) + x] * scale;
            }
        }
    }

    private static void ChromaFromLumaLf(float[] cx, float[] cy, float[] cb, int len, LfChannelCorrelation corr)
    {
        int xFactor = corr.XFactorLf - 128;
        int bFactor = corr.BFactorLf - 128;
        float kx = corr.BaseCorrelationX + (xFactor / (float)corr.ColourFactor);
        float kb = corr.BaseCorrelationB + (bFactor / (float)corr.ColourFactor);
        for (int i = 0; i < len; i++)
        {
            float y = cy[i];
            cx[i] += kx * y;
            cb[i] += kb * y;
        }
    }

    private static void AdaptiveLfSmoothing(float[][] lf, int width, int height, float[] lfScale)
    {
        const float scaleSelf = 0.052262735f;
        const float scaleSide = 0.2034514f;
        const float scaleDiag = 0.03348292f;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var udsum = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            udsum[c] = new float[width * (height - 2)];
            float[] g = lf[c];
            for (int y = 0; y < height - 2; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    udsum[c][(y * width) + x] = g[(y * width) + x] + g[((y + 2) * width) + x];
                }
            }
        }

        for (int y = 1; y < height - 1; y++)
        {
            int ur = (y - 1) * width; // udsum row index base for center row y
            float[] px = lf[0], py = lf[1], pb = lf[2];
            float xPrev = px[(y * width) + 0];
            float yPrev = py[(y * width) + 0];
            float bPrev = pb[(y * width) + 0];
            for (int x = 1; x < width - 1; x++)
            {
                int i = (y * width) + x;
                float xSelf = px[i];
                float xSide = xPrev + px[i + 1] + udsum[0][ur + x];
                float xDiag = udsum[0][ur + x - 1] + udsum[0][ur + x + 1];
                float xWa = (xSelf * scaleSelf) + (xSide * scaleSide) + (xDiag * scaleDiag);
                float xGap = MathF.Abs(xWa - xSelf) / lfScale[0];

                float ySelf = py[i];
                float ySide = yPrev + py[i + 1] + udsum[1][ur + x];
                float yDiag = udsum[1][ur + x - 1] + udsum[1][ur + x + 1];
                float yWa = (ySelf * scaleSelf) + (ySide * scaleSide) + (yDiag * scaleDiag);
                float yGap = MathF.Abs(yWa - ySelf) / lfScale[1];

                float bSelf = pb[i];
                float bSide = bPrev + pb[i + 1] + udsum[2][ur + x];
                float bDiag = udsum[2][ur + x - 1] + udsum[2][ur + x + 1];
                float bWa = (bSelf * scaleSelf) + (bSide * scaleSide) + (bDiag * scaleDiag);
                float bGap = MathF.Abs(bWa - bSelf) / lfScale[2];

                float gap = MathF.Max(0.5f, MathF.Max(xGap, MathF.Max(yGap, bGap)));
                float gapScale = MathF.Max(0f, 3.0f - (4.0f * gap));
                px[i] = ((xWa - xSelf) * gapScale) + xSelf;
                py[i] = ((yWa - ySelf) * gapScale) + ySelf;
                pb[i] = ((bWa - bSelf) * gapScale) + bSelf;
                xPrev = xSelf; yPrev = ySelf; bPrev = bSelf;
            }
        }
    }

    private static void ChromaFromLumaHf(float[][] coeff, int stride, int gw, int gh, LfGroupData lg, LfChannelCorrelation corr)
    {
        for (int y = 0; y < gh; y++)
        {
            int y64 = y / 64;
            for (int x64 = 0; x64 * 64 < gw; x64++)
            {
                int kxRaw = lg.XFromY[(y64 * lg.XFromYW) + x64];
                int kbRaw = lg.BFromY[(y64 * lg.XFromYW) + x64];
                float kx = corr.BaseCorrelationX + (kxRaw / (float)corr.ColourFactor);
                float kb = corr.BaseCorrelationB + (kbRaw / (float)corr.ColourFactor);
                int maxDx = Math.Min(64, gw - (x64 * 64));
                for (int dx = 0; dx < maxDx; dx++)
                {
                    int x = (x64 * 64) + dx;
                    int i = (y * stride) + x;
                    float cyv = coeff[1][i];
                    coeff[0][i] += kx * cyv;
                    coeff[2][i] += kb * cyv;
                }
            }
        }
    }

    private static readonly (int, int)[] EpfKernel1 = { (0, -1), (0, 1), (-1, 0), (1, 0) };
    private static readonly (int, int)[] EpfDistStep1 = { (0, -1), (0, 0), (0, 1), (-1, 0), (1, 0) };
    private static readonly (int, int)[] EpfDistStep2 = { (0, 0) };

    private static int Mirror(int off, int len)
    {
        while (true)
        {
            if (off < 0)
            {
                off = -(off + 1);
            }
            else if (off >= len)
            {
                off = (len * 2) - off - 1;
            }
            else
            {
                return off;
            }
        }
    }

    /// <summary>One EPF pass (step 1 or 2): SAD-weighted edge-preserving smoothing over all 3 XYB channels.</summary>
    private static void EpfPass(float[][] src, float[][] dst, int w, int h, int stride, float[] sigma, int blocksW, int step, float stepMult, VarDctFrameParams fp)
    {
        (int, int)[] kernel = EpfKernel1;
        (int, int)[] dist = step == 2 ? EpfDistStep2 : EpfDistStep1;
        float borderSad = fp.EpfBorderSadMul;
        float[] cs = fp.EpfChannelScale;
        const float invSqrt2 = 0.70710678f;
        for (int y = 0; y < h; y++)
        {
            bool yBorder = ((y + 1) & 6) == 0;
            for (int x = 0; x < w; x++)
            {
                float sig = sigma[((y / 8) * blocksW) + (x / 8)];
                int di = (y * stride) + x;
                if (sig < 0.3f)
                {
                    dst[0][di] = src[0][di];
                    dst[1][di] = src[1][di];
                    dst[2][di] = src[2][di];
                    continue;
                }

                float sm;
                if (yBorder)
                {
                    sm = stepMult * borderSad;
                }
                else
                {
                    int xm = x & 7;
                    sm = (xm == 0 || xm == 7) ? stepMult * borderSad : stepMult;
                }

                float negInvSigma = 6.6f * (invSqrt2 - 1.0f) / sig * sm;
                float sumW = 1.0f;
                float s0 = src[0][di], s1 = src[1][di], s2 = src[2][di];
                foreach ((int kx, int ky) in kernel)
                {
                    float d = 0f;
                    for (int c = 0; c < 3; c++)
                    {
                        float acc = 0f;
                        float[] sc = src[c];
                        foreach ((int ix, int iy) in dist)
                        {
                            int kyy = Mirror(y + ky + iy, h);
                            int kxx = Mirror(x + kx + ix, w);
                            int byy = Mirror(y + iy, h);
                            int bxx = Mirror(x + ix, w);
                            acc += Math.Abs(sc[(kyy * stride) + kxx] - sc[(byy * stride) + bxx]);
                        }

                        d += cs[c] * acc;
                    }

                    float weight = 1.0f + (d * negInvSigma);
                    if (weight < 0f)
                    {
                        weight = 0f;
                    }

                    sumW += weight;
                    int ny = Mirror(y + ky, h);
                    int nx = Mirror(x + kx, w);
                    s0 += weight * src[0][(ny * stride) + nx];
                    s1 += weight * src[1][(ny * stride) + nx];
                    s2 += weight * src[2][(ny * stride) + nx];
                }

                dst[0][di] = s0 / sumW;
                dst[1][di] = s1 / sumW;
                dst[2][di] = s2 / sumW;
            }
        }
    }

    /// <summary>Gaborish 3x3 smoothing filter (edge pixels clamped/replicated), applied per XYB channel in place.</summary>
    private static void Gaborish(float[][] xyb, int w, int h, int stride, VarDctFrameParams fp)
    {
        for (int c = 0; c < 3; c++)
        {
            float w0 = fp.GabWeights[c][0];
            float w1 = fp.GabWeights[c][1];
            float gw = 1.0f / (1.0f + (4.0f * w0) + (4.0f * w1));
            float[] src = xyb[c];
            var dst = new float[src.Length];
            for (int y = 0; y < h; y++)
            {
                int yt = y > 0 ? y - 1 : 0;
                int yb = y < h - 1 ? y + 1 : h - 1;
                for (int x = 0; x < w; x++)
                {
                    int xl = x > 0 ? x - 1 : 0;
                    int xr = x < w - 1 ? x + 1 : w - 1;
                    float t0 = src[(yt * stride) + xl], t1 = src[(yt * stride) + x], t2 = src[(yt * stride) + xr];
                    float c0 = src[(y * stride) + xl], c1 = src[(y * stride) + x], c2 = src[(y * stride) + xr];
                    float b0 = src[(yb * stride) + xl], b1 = src[(yb * stride) + x], b2 = src[(yb * stride) + xr];
                    float sumSide = t1 + c0 + c2 + b1;
                    float sumDiag = t0 + t2 + b0 + b2;
                    dst[(y * stride) + x] = (c1 + (sumSide * w0) + (sumDiag * w1)) * gw;
                }
            }

            xyb[c] = dst;
        }
    }

    private static void XybToSrgb(float[][] xyb, int len, VarDctFrameParams fp)
    {
        float itscale = 255.0f / fp.IntensityTarget;
        float[] ob = fp.OpsinBias;
        float[] cbrtOb = { MathF.Cbrt(ob[0]), MathF.Cbrt(ob[1]), MathF.Cbrt(ob[2]) };
        float[] m = fp.OpsinInv;
        for (int i = 0; i < len; i++)
        {
            float xv = xyb[0][i];
            float yv = xyb[1][i];
            float bv = xyb[2][i];
            float gl = yv + xv - cbrtOb[0];
            float gm = yv - xv - cbrtOb[1];
            float gs = bv - cbrtOb[2];
            float lms0 = ((gl * gl * gl) + ob[0]) * itscale;
            float lms1 = ((gm * gm * gm) + ob[1]) * itscale;
            float lms2 = ((gs * gs * gs) + ob[2]) * itscale;
            // opsin inverse matrix -> linear sRGB
            float r = (m[0] * lms0) + (m[1] * lms1) + (m[2] * lms2);
            float g = (m[3] * lms0) + (m[4] * lms1) + (m[5] * lms2);
            float bl = (m[6] * lms0) + (m[7] * lms1) + (m[8] * lms2);
            xyb[0][i] = LinearToSrgb(r);
            xyb[1][i] = LinearToSrgb(g);
            xyb[2][i] = LinearToSrgb(bl);
        }
    }

    private static float LinearToSrgb(float v)
    {
        if (v <= 0f)
        {
            return 0f;
        }

        if (v >= 1f)
        {
            return 1f;
        }

        return v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f;
    }

    /// <summary>Decodes a VarDCT frame body. Sections are laid out per the TOC; returns 3 sRGB float channels [0,1].</summary>
    public static float[][] Decode(byte[] data, int[] offsets, uint[] sizes, VarDctFrameParams fp, List<MaNode>? gTreeIn, JxlAnsCode? gCodeIn)
    {
        bool single = offsets.Length == 1;
        int w = fp.Width, h = fp.Height;
        int stride = ((w + 7) / 8) * 8; // rounded up to block
        int strideH = ((h + 7) / 8) * 8;

        // Section reader helpers.
        JxlBitReader SectionReader(int idx) => new(data, offsets[idx]);

        // --- LfGlobal (section 0) ---
        JxlBitReader lb = SectionReader(0);
        // flags -> patches/splines/noise (not supported yet; assume flags handled by caller = 0).
        float[] lfDequant = ReadLfChannelDequant(lb);
        Quantizer quant = ReadQuantizer(lb);
        HfBlockContext hfCtx = ReadHfBlockContext(lb);
        LfChannelCorrelation corr = ReadLfChannelCorrelation(lb);

        // GlobalModular: has_tree + tree + GroupHeader + extra channels (none in the common case).
        bool hasTree = lb.ReadBits(1) != 0;
        List<MaNode>? gTree = null;
        JxlAnsCode? gCode = null;
        if (hasTree)
        {
            gTree = JxlModular.DecodeTree(lb);
            gCode = JxlEntropy.DecodeHistograms((gTree.Count + 1) / 2, lb);
        }

        // GroupHeader for gmodular (0 color channels for VarDCT, no extra channels assumed).
        var emptyChans = new List<JxlChannel>();
        JxlModular.DecodeSubModular(lb, emptyChans, gTree, gCode, 0, fp.BitDepth);

        // For single-section frames, everything continues in `lb`. For multi-section, use offsets.
        // --- LfGroups ---
        var lfGroups = new LfGroupData[fp.NumLf];
        int lfGroupDim = fp.GroupDim * 8;
        int lfgPerRow = (w + lfGroupDim - 1) / lfGroupDim;
        for (int lg = 0; lg < fp.NumLf; lg++)
        {
            JxlBitReader r = single ? lb : SectionReader(1 + lg);
            int gx = lg % lfgPerRow;
            int gy = lg / lfgPerRow;
            int lfW = Math.Min(lfGroupDim, w - (gx * lfGroupDim));
            int lfH = Math.Min(lfGroupDim, h - (gy * lfGroupDim));
            lfGroups[lg] = ReadLfGroup(r, fp, lg, lfW, lfH, gTree, gCode, quant, lfDequant, hfCtx);
        }

        // --- HfGlobal ---
        JxlBitReader hb = single ? lb : SectionReader(1 + fp.NumLf);
        DequantMatrixSet dm = DequantMatrixSet.Parse(hb, fp.BitDepth, fp.NumLf, gTree, gCode);
        int numHfPresets = (int)hb.ReadBits(BitLength((uint)NextPow2(fp.NumGroups))) + 1;
        var hfPasses = new HfPass[fp.NumPasses];
        for (int p = 0; p < fp.NumPasses; p++)
        {
            hfPasses[p] = ReadHfPass(hb, hfCtx, numHfPresets);
        }

        // --- Build LF (DC) images per LF group, dequant + CfL + adaptive smoothing ---
        // For simplicity handle a single LF group covering the whole image (common for <=2048px).
        // Assemble full LF image.
        int lfFullW = (w + 7) / 8;
        int lfFullH = (h + 7) / 8;
        var lfXyb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            lfXyb[c] = new float[lfFullW * lfFullH];
        }

        // DC modular channels are stored as [Y, X, B]; XYB output channel c reads DC channel dcIdx[c].
        int[] dcIdx = { 1, 0, 2 };
        float[] mlf = { lfDequant[0], lfDequant[1], lfDequant[2] }; // m_x_lf, m_y_lf, m_b_lf
        for (int lg = 0; lg < fp.NumLf; lg++)
        {
            LfGroupData g = lfGroups[lg];
            int gx = lg % lfgPerRow;
            int gy = lg / lfgPerRow;
            int lfBaseX = gx * (lfGroupDim / 8);
            int lfBaseY = gy * (lfGroupDim / 8);
            for (int c = 0; c < 3; c++)
            {
                var tmp = new float[g.LfW * g.LfH];
                CopyLfDequant(tmp, g.LfW, g.LfW, g.LfH, quant, mlf[c], g.LfQuant[dcIdx[c]], g.ExtraPrecision);
                for (int y = 0; y < g.LfH; y++)
                {
                    for (int x = 0; x < g.LfW; x++)
                    {
                        lfXyb[c][((lfBaseY + y) * lfFullW) + lfBaseX + x] = tmp[(y * g.LfW) + x];
                    }
                }
            }
        }

        ChromaFromLumaLf(lfXyb[0], lfXyb[1], lfXyb[2], lfFullW * lfFullH, corr);
        if (!fp.SkipAdaptiveLfSmoothing)
        {
            double scaleInv = (double)quant.GlobalScale * quant.QuantLf;
            float[] lfScale = { (float)(512.0 * lfDequant[0] / scaleInv), (float)(512.0 * lfDequant[1] / scaleInv), (float)(512.0 * lfDequant[2] / scaleInv) };
            AdaptiveLfSmoothing(lfXyb, lfFullW, lfFullH, lfScale);
        }

        // --- PassGroups: decode HF coefficients, dequant, CfL, transform ---
        var outXyb = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            outXyb[c] = new float[stride * strideH];
        }

        for (int grp = 0; grp < fp.NumGroups; grp++)
        {
            int gx = grp % fp.GroupsPerRow;
            int gy = grp / fp.GroupsPerRow;
            int lfGroupIdx = LfGroupIdxFromGroup(fp, grp);
            LfGroupData lg = lfGroups[lfGroupIdx];

            // per-group coefficient grids (group_dim x group_dim)
            int gpw = Math.Min(fp.GroupDim, w - (gx * fp.GroupDim));
            int gph = Math.Min(fp.GroupDim, h - (gy * fp.GroupDim));
            int gStride = ((gpw + 7) / 8) * 8;
            int gH = ((gph + 7) / 8) * 8;
            var coeff = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                coeff[c] = new float[gStride * gH];
            }

            // NOTE: block_info for the group is a subgrid of the LF group; for single-group frames it's the whole thing.
            var groupLg = SubLfGroup(lg, fp, grp);

            for (int p = 0; p < fp.NumPasses; p++)
            {
                JxlBitReader pb = single ? lb : SectionReader(2 + fp.NumLf + (p * fp.NumGroups) + grp);
                WriteHfCoeff(pb, fp, hfCtx, hfPasses[p], groupLg, numHfPresets, coeff, gStride);
            }

            DequantHf(fp, hfCtx, dm, quant, groupLg, coeff, gStride);
            ChromaFromLumaHf(coeff, gStride, gStride, gH, groupLg, corr);

            // transform each varblock adding LF DC, write into outXyb.
            TransformGroup(coeff, gStride, groupLg, lfXyb, lfFullW, gx, gy, fp, outXyb, stride);
        }

        if (fp.GabEnabled)
        {
            Gaborish(outXyb, stride, strideH, stride, fp);
        }

        if (fp.EpfIters > 0)
        {
            // Assemble a full-image per-block sigma grid from the LF groups.
            int blocksW = stride / 8;
            int blocksH = strideH / 8;
            var fullSigma = new float[blocksW * blocksH];
            for (int lg = 0; lg < fp.NumLf; lg++)
            {
                LfGroupData g = lfGroups[lg];
                int gx = lg % lfgPerRow;
                int gy = lg / lfgPerRow;
                int bBaseX = gx * (lfGroupDim / 8);
                int bBaseY = gy * (lfGroupDim / 8);
                for (int by = 0; by < g.Bh; by++)
                {
                    for (int bx = 0; bx < g.Bw; bx++)
                    {
                        int fx = bBaseX + bx, fy = bBaseY + by;
                        if (fx < blocksW && fy < blocksH)
                        {
                            fullSigma[(fy * blocksW) + fx] = g.EpfSigma[(by * g.Bw) + bx];
                        }
                    }
                }
            }

            var epfTmp = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                epfTmp[c] = new float[stride * strideH];
            }

            float[][] a = outXyb, b = epfTmp;
            if (fp.EpfIters == 3)
            {
                EpfPass(a, b, stride, strideH, stride, fullSigma, blocksW, 0, fp.EpfPass0SigmaScale, fp);
                (a, b) = (b, a);
            }

            EpfPass(a, b, stride, strideH, stride, fullSigma, blocksW, 1, 1.0f, fp);
            (a, b) = (b, a);
            if (fp.EpfIters >= 2)
            {
                EpfPass(a, b, stride, strideH, stride, fullSigma, blocksW, 2, fp.EpfPass2SigmaScale, fp);
                (a, b) = (b, a);
            }

            outXyb = a;
        }

        XybToSrgb(outXyb, stride * strideH, fp);

        // crop to actual width/height into contiguous w*h buffers
        var result = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            result[c] = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(outXyb[c], y * stride, result[c], y * w, w);
            }
        }

        return result;
    }

    private static int LfGroupIdxFromGroup(VarDctFrameParams fp, int groupIdx)
    {
        int lfGroupDim = fp.GroupDim * 8;
        int groupsPerLfRow = (fp.Width + lfGroupDim - 1) / lfGroupDim;
        int gx = groupIdx % fp.GroupsPerRow;
        int gy = groupIdx / fp.GroupsPerRow;
        int groupsPerLf = lfGroupDim / fp.GroupDim; // groups per lf group in one dim
        int lgx = gx / groupsPerLf;
        int lgy = gy / groupsPerLf;
        return (lgy * groupsPerLfRow) + lgx;
    }

    // For single-group-per-lf-group frames the group's block info == the LF group's block info.
    private static LfGroupData SubLfGroup(LfGroupData lg, VarDctFrameParams fp, int groupIdx)
    {
        return lg; // common case: one group per LF group (image <= group_dim*8)
    }

    private static void TransformGroup(float[][] coeff, int coeffStride, LfGroupData lg, float[][] lfXyb, int lfStride, int gx, int gy, VarDctFrameParams fp, float[][] outXyb, int outStride)
    {
        int lfBaseX = gx * (fp.GroupDim / 8);
        int lfBaseY = gy * (fp.GroupDim / 8);
        int outBaseX = gx * fp.GroupDim;
        int outBaseY = gy * fp.GroupDim;
        for (int c = 0; c < 3; c++)
        {
            for (int by = 0; by < lg.Bh; by++)
            {
                for (int bx = 0; bx < lg.Bw; bx++)
                {
                    ref BlockInfo bi = ref lg.BlockInfoGrid[(by * lg.Bw) + bx];
                    if (!bi.IsData)
                    {
                        continue;
                    }

                    var cg = new JxlDct.Grid(coeff[c], (by * 8 * coeffStride) + (bx * 8), coeffStride, 0, 0);
                    // LF DC source position in the full LF image
                    int lfx = lfBaseX + bx;
                    int lfy = lfBaseY + by;
                    JxlDct.TransformVarblock(cg, bi.Dct, lfXyb[c], lfStride, lfx, lfy);
                }
            }

            // copy group spatial pixels into output
            int gpw = coeffStride;
            for (int y = 0; y < lg.Bh * 8 && outBaseY + y < outXyb[c].Length / outStride; y++)
            {
                int copyW = Math.Min(coeffStride, outStride - outBaseX);
                if (copyW > 0)
                {
                    Array.Copy(coeff[c], y * coeffStride, outXyb[c], ((outBaseY + y) * outStride) + outBaseX, copyW);
                }
            }
        }
    }
}
