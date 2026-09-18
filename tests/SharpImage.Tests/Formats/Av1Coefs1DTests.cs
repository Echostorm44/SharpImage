using System;
using SharpImage.Formats.Av1;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// Byte-exact gate for the 1D-class (V_DCT / H_DCT) coefficient coder: for random quantized coefficient blocks,
// Av1CoeffEncode.EncodeCoefs1D must produce a bitstream that Av1CoeffDecode.DecodeCoefs (our dav1d-verified
// decoder) reads back to the identical levels + tx type. This isolates the desync-critical 1D coeff path from the
// rest of the pipeline. dq is set to 1 (dqShift=0 for 4x4/8x8) so the decoded, dequantized cf equals the input.
public sealed class Av1Coefs1DTests
{
    [Test]
    [Arguments(0)] // TX_4X4
    [Arguments(1)] // TX_8X8
    public async Task VDct_HDct_CoeffRoundTrip(int tx)
    {
        ref readonly Av1TxfmInfo tDim = ref Av1Tables.TxfmDimensions[tx];
        int slw = Math.Min((int)tDim.Lw, 5), slh = Math.Min((int)tDim.Lh, 5);
        int area = (4 << slw) * (4 << slh);
        int cfLen = Av1Tables.Scans[tx].Length;
        int bs = tx == 0 ? (int)Av1BlockSize.Bs4x4 : (int)Av1BlockSize.Bs8x8;
        int shiftH = slh + 2, maskH = (4 << slh) - 1;                 // Horizontal geometry
        int shiftV = slw + 2, shift2V = slh + 2, maskV = (4 << slw) - 1; // Vertical geometry
        var dq = new ushort[] { 1, 1 };
        var qm = ReadOnlySpan<byte>.Empty;

        int mismatches = 0;
        foreach (var txtp in new[] { Av1TxType.VDct, Av1TxType.HDct })
        {
            bool horiz = txtp == Av1TxType.HDct;
            for (int seed = 0; seed < 40; seed++)
            {
                var rng = new Random(seed * 131 + (int)txtp * 7 + tx);
                var cf = new int[cfLen];
                int nnz = rng.Next(0, 8);
                for (int k = 0; k < nnz; k++)
                {
                    int i = rng.Next(0, area);
                    int x = i & (horiz ? maskH : maskV);
                    int y = i >> (horiz ? shiftH : shiftV);
                    int rc = horiz ? i : ((x << shift2V) | y);
                    int lvl = rng.Next(1, seed % 5 == 0 ? 40 : 6); // occasionally >=15 to exercise Golomb
                    cf[rc] = rng.Next(2) == 0 ? -lvl : lvl;
                }

                // Encode.
                var wcdf = new Av1CdfContext();
                Av1CdfDefaults.InitializeDefault(wcdf, 1);
                var a = new byte[32];
                var l = new byte[32];
                int skipCtx = Av1CoeffDecode.GetSkipCtx(in tDim, bs, a, l, 0, (int)Av1PixelLayout.I420);
                int dcSignCtx = Av1CoeffDecode.GetDcSignCtx(tx, a, l);
                var w = new Av1MsacWriter();
                Av1CoeffEncode.EncodeCoefs1D(w, wcdf.Coef, wcdf.Mode, tx, 0, txtp, cf, skipCtx, dcSignCtx);
                byte[] bytes = w.Finish();

                // Decode with a fresh identical CDF (both sides adapt from the same start).
                var rcdf = new Av1CdfContext();
                Av1CdfDefaults.InitializeDefault(rcdf, 1);
                var r = new Av1Msac(bytes, disableCdfUpdate: false);
                var outCf = new int[cfLen];
                var levels = new byte[16 * 40];
                Av1TxType dtxtp = Av1TxType.DctDct;
                Av1CoeffDecode.DecodeCoefs(ref r, rcdf.Coef, rcdf.Mode, a, l, tx, bs, 0, 0, 0, 0,
                    1, 0, outCf, ref dtxtp, out _, dq, qm, false, false, 100, 8, levels, (int)Av1PixelLayout.I420);

                bool anyNz = false; foreach (var v in cf) if (v != 0) { anyNz = true; break; }
                // An all-zero block is coded as skip: no tx-type symbol, decoder returns Dct_Dct — that's correct.
                if (anyNz && dtxtp != txtp) { mismatches++; if (mismatches == 1) System.Console.WriteLine($"[1D] tx={tx} {txtp} seed={seed} TXTP MISMATCH got={dtxtp} nnz={nnz}"); continue; }
                for (int rc = 0; rc < cfLen; rc++) if (outCf[rc] != cf[rc])
                {
                    mismatches++;
                    if (mismatches == 1)
                    {
                        var sb = new System.Text.StringBuilder($"[1D] tx={tx} {txtp} seed={seed} rc={rc} exp={cf[rc]} got={outCf[rc]} | in:");
                        for (int q = 0; q < cfLen; q++) if (cf[q] != 0) sb.Append($" {q}={cf[q]}");
                        sb.Append(" out:");
                        for (int q = 0; q < cfLen; q++) if (outCf[q] != 0) sb.Append($" {q}={outCf[q]}");
                        System.Console.WriteLine(sb.ToString());
                    }
                    break;
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }
}
