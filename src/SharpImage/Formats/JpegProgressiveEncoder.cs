// Byte-exact re-encoder for PROGRESSIVE (SOF2) JPEG entropy scans, the inverse of the progressive decoder in
// JpegCoder.cs. It reproduces libjpeg jcphuff's exact bitstream: DC first/refinement, AC first (with EOBRUN
// grouping), and AC refinement (with the buffered-correction-bit / BE mechanism). Each scan's verbatim prefix
// (inter-scan markers + SOS header) is emitted as-is; only the entropy-coded data is regenerated from the final
// coefficients. Given a libjpeg-family source, the output is bit-identical; a self-verify guard rejects any
// file this cannot reproduce, so a mismatch is a clean refusal, never a corrupt result.
using System;
using System.Collections.Generic;
using System.IO;
using SharpImage.Compression;

namespace SharpImage.Formats;

public static partial class JpegCoder
{
    private static byte[] RebuildProgressiveJpeg(JpegDctData d)
    {
        var dcEnc = new (int Code, int Len)[4][];
        var acEnc = new (int Code, int Len)[4][];

        using var ms = new MemoryStream();
        foreach (JpegScan scan in d.Scans)
        {
            ms.Write(scan.Prefix, 0, scan.Prefix.Length);
            ParseHuffmanEncodeTables(scan.Prefix, 0, dcEnc, acEnc); // (re)definitions carried into this scan
            EncodeProgressiveScan(ms, d, scan, dcEnc, acEnc);
        }

        ms.Write(d.TrailingBytes, 0, d.TrailingBytes.Length);
        return ms.ToArray();
    }

    // Point transform: DC uses the arithmetic (floor) shift the decoder's `pred << Al` / `|=` reconstruction
    // implies; AC uses truncation toward zero (sign-magnitude), matching `value << Al` + sign-magnitude refine.
    private static int PointTransformAc(int coef, int al) =>
        coef >= 0 ? coef >> al : -((-coef) >> al);

    private static void EncodeProgressiveScan(Stream stream, JpegDctData d, JpegScan scan,
        (int Code, int Len)[][] dcEnc, (int Code, int Len)[][] acEnc)
    {
        int mcuWidth = d.MaxHSample * BlockSize, mcuHeight = d.MaxVSample * BlockSize;
        int mcuCols = (d.Width + mcuWidth - 1) / mcuWidth;
        int mcuRows = (d.Height + mcuHeight - 1) / mcuHeight;
        var writer = new JpegBitWriter(stream);
        int restartInterval = scan.RestartInterval;
        int restartCounter = 0, mcuCount = 0;

        if (scan.Ss == 0)
        {
            // DC scan: interleaved over MCUs, all scan components, in MCU/block order (mirrors the decoder).
            var dcPred = new int[scan.ComponentIndices.Length];
            for (int mcuRow = 0; mcuRow < mcuRows; mcuRow++)
            {
                for (int mcuCol = 0; mcuCol < mcuCols; mcuCol++)
                {
                    if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                    {
                        writer.Flush();
                        stream.WriteByte(0xFF);
                        stream.WriteByte((byte)(0xD0 + (restartCounter++ & 7)));
                        Array.Clear(dcPred);
                    }

                    for (int si = 0; si < scan.ComponentIndices.Length; si++)
                    {
                        int c = scan.ComponentIndices[si];
                        JpegDctComponent comp = d.Components[c];
                        int strideH = mcuCols * comp.HSample;
                        for (int bv = 0; bv < comp.VSample; bv++)
                        {
                            for (int bh = 0; bh < comp.HSample; bh++)
                            {
                                int blockRow = (mcuRow * comp.VSample) + bv;
                                int blockCol = (mcuCol * comp.HSample) + bh;
                                int[] block = comp.Blocks[(blockRow * strideH) + blockCol];
                                if (scan.Ah == 0)
                                {
                                    int v = block[0] >> scan.Al; // arithmetic (floor) high bits
                                    int diff = v - dcPred[si];
                                    dcPred[si] = v;
                                    EncodeDcCoefficient(writer, diff, dcEnc[scan.CompDcTable[si]]);
                                }
                                else
                                {
                                    writer.WriteBits((block[0] >> scan.Al) & 1, 1);
                                }
                            }
                        }
                    }

                    mcuCount++;
                }
            }

            writer.Flush();
            return;
        }

        // AC scan: single component, non-interleaved — iterate the component's OWN block grid (mirrors decoder).
        int cc = scan.ComponentIndices[0];
        JpegDctComponent accomp = d.Components[cc];
        int acStride = mcuCols * accomp.HSample;
        int compSamplesW = (d.Width * accomp.HSample + d.MaxHSample - 1) / d.MaxHSample;
        int compSamplesH = (d.Height * accomp.VSample + d.MaxVSample - 1) / d.MaxVSample;
        int totalBlocksH = (compSamplesW + BlockSize - 1) / BlockSize;
        int totalBlocksV = (compSamplesH + BlockSize - 1) / BlockSize;
        var ac = new ProgAcState(writer, acEnc[scan.CompAcTable[0]]);

        for (int blockRow = 0; blockRow < totalBlocksV; blockRow++)
        {
            for (int blockCol = 0; blockCol < totalBlocksH; blockCol++)
            {
                if (restartInterval > 0 && mcuCount > 0 && mcuCount % restartInterval == 0)
                {
                    ac.EmitEobrun();
                    writer.Flush();
                    stream.WriteByte(0xFF);
                    stream.WriteByte((byte)(0xD0 + (restartCounter++ & 7)));
                }

                int[] block = accomp.Blocks[(blockRow * acStride) + blockCol];
                if (scan.Ah == 0)
                {
                    ac.EncodeAcFirst(block, scan.Ss, scan.Se, scan.Al);
                }
                else
                {
                    ac.EncodeAcRefine(block, scan.Ss, scan.Se, scan.Al);
                }

                mcuCount++;
            }
        }

        ac.EmitEobrun();
        writer.Flush();
    }

    // Stateful AC-scan encoder holding the cross-block EOBRUN counter and the buffered correction bits (BE),
    // exactly as libjpeg's phuff_entropy does — required to reproduce the original bitstream byte-for-byte.
    private sealed class ProgAcState
    {
        private readonly JpegBitWriter writer;
        private readonly (int Code, int Len)[] acTable;
        private int eobrun;
        private readonly List<int> committedBits = new(); // BE: correction bits for pending EOB-run blocks
        private readonly List<int> blockBits = new();     // BR: correction bits for the current block

        public ProgAcState(JpegBitWriter writer, (int Code, int Len)[] acTable)
        {
            this.writer = writer;
            this.acTable = acTable;
        }

        private void EmitSymbol(int symbol)
        {
            (int code, int len) = acTable[symbol];
            writer.WriteBits(code, len);
        }

        // Flush a pending EOB run (its length symbol + extra bits) and any correction bits buffered against it.
        public void EmitEobrun()
        {
            if (eobrun <= 0)
            {
                return;
            }

            int temp = eobrun, nbits = 0;
            while ((temp >>= 1) != 0)
            {
                nbits++;
            }

            EmitSymbol(nbits << 4);
            if (nbits > 0)
            {
                writer.WriteBits(eobrun & ((1 << nbits) - 1), nbits);
            }

            eobrun = 0;
            foreach (int b in committedBits)
            {
                writer.WriteBits(b, 1);
            }

            committedBits.Clear();
        }

        // First AC scan for one block: run/size coding with EOBRUN grouping across blocks (jcphuff AC_first).
        public void EncodeAcFirst(int[] block, int ss, int se, int al)
        {
            int r = 0;
            for (int k = ss; k <= se; k++)
            {
                int t = PointTransformAc(block[JpegTables.NaturalOrder[k]], al);
                if (t == 0)
                {
                    r++;
                    continue;
                }

                EmitEobrun();
                while (r > 15)
                {
                    EmitSymbol(0xF0);
                    r -= 16;
                }

                int abs = t < 0 ? -t : t;
                int nbits = 0;
                for (int v = abs; v != 0; v >>= 1)
                {
                    nbits++;
                }

                EmitSymbol((r << 4) + nbits);
                int coded = t >= 0 ? t : t + (1 << nbits) - 1; // JPEG signed-magnitude extra bits
                writer.WriteBits(coded, nbits);
                r = 0;
            }

            if (r > 0)
            {
                eobrun++;
                if (eobrun == 0x7FFF)
                {
                    EmitEobrun();
                }
            }
        }

        // Refinement AC scan for one block: correction bits for already-significant coefficients + run/size(1)
        // for newly-significant ones, with buffered correction bits carried into the EOB run (jcphuff AC_refine).
        public void EncodeAcRefine(int[] block, int ss, int se, int al)
        {
            // Pre-pass: transformed magnitudes and EOB = index of the last newly-significant coefficient.
            int eob = 0;
            Span<int> absValues = se - ss + 1 <= 64 ? stackalloc int[se - ss + 1] : new int[se - ss + 1];
            for (int k = ss; k <= se; k++)
            {
                int coef = block[JpegTables.NaturalOrder[k]];
                int t = (coef < 0 ? -coef : coef) >> al; // absolute value, point-transformed
                absValues[k - ss] = t;
                if (t == 1)
                {
                    eob = k;
                }
            }

            int r = 0;
            for (int k = ss; k <= se; k++)
            {
                int t = absValues[k - ss];
                if (t == 0)
                {
                    r++;
                    continue;
                }

                while (r > 15 && k <= eob)
                {
                    EmitEobrun();
                    EmitSymbol(0xF0);
                    r -= 16;
                    FlushBlockBits();
                }

                if (t > 1)
                {
                    blockBits.Add(t & 1); // correction bit for an already-significant coefficient
                    continue;
                }

                EmitEobrun();
                EmitSymbol((r << 4) + 1);
                writer.WriteBits(block[JpegTables.NaturalOrder[k]] < 0 ? 0 : 1, 1); // sign of new coefficient
                FlushBlockBits();
                r = 0;
            }

            if (r > 0 || blockBits.Count > 0)
            {
                eobrun++;
                committedBits.AddRange(blockBits);
                blockBits.Clear();
                // Force out the EOB run before the counter or the correction-bit buffer can overflow
                // (libjpeg: MAX_CORR_BITS=1000, DCTSIZE2=64), so the two sides stay bit-identical.
                if (eobrun == 0x7FFF || committedBits.Count > 1000 - 64 + 1)
                {
                    EmitEobrun();
                }
            }
        }

        // Emit the current block's buffered correction bits (associated with the symbol just written) and clear.
        private void FlushBlockBits()
        {
            foreach (int b in blockBits)
            {
                writer.WriteBits(b, 1);
            }

            blockBits.Clear();
        }
    }
}
