using SharpImage.Formats.Hevc;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Round-trip tests for the HEVC intra-encoder building blocks (CABAC engine, forward transform +
/// quantization, sign-data-hiding, residual_coding), each verified against the existing HEVC decoder
/// which is their exact mirror. These back the from-scratch HEIC encoder.
/// </summary>
public class HevcEncoderTests
{
    [Test]
    public async Task Cabac_Encoder_RoundTrips_Through_Decoder()
    {
        int nCtx = HevcCabacContextIndex.TotalContexts;
        var rng = new Random(1234);
        const int qp = 27, initType = 0, n = 20000;
        var ops = new (int kind, int ctx, int val)[n];
        for (int i = 0; i < n; i++)
        {
            ops[i] = (rng.Next(4) == 0 ? 1 : 0, rng.Next(nCtx), rng.Next(2));
        }

        var w = new HevcBitWriter();
        var enc = new HevcCabacEncoder(w);
        enc.InitializeContexts(qp, initType);
        enc.Start();
        foreach ((int kind, int ctx, int val) in ops)
        {
            if (kind == 0)
            {
                enc.EncodeBin(ctx, val);
            }
            else
            {
                enc.EncodeBypass(val);
            }
        }

        enc.EncodeTerminate(1);
        enc.Finish();
        w.ByteAlignWithStopBit();

        int mismatches;
        int terminate;
        {
            var ctxStorage = new byte[nCtx];
            var dec = new HevcCabacDecoder(w.ToArray(), ctxStorage, qp, initType);
            int mm = 0;
            for (int i = 0; i < n; i++)
            {
                int got = ops[i].kind == 0 ? dec.DecodeBin(ops[i].ctx) : dec.DecodeBypass();
                if (got != ops[i].val)
                {
                    mm++;
                }
            }

            mismatches = mm;
            terminate = dec.DecodeTerminate();
        }

        await Assert.That(mismatches).IsEqualTo(0);
        await Assert.That(terminate).IsEqualTo(1);
    }

    [Test]
    public async Task ForwardTransform_And_Quant_RoundTrip_Within_QuantError()
    {
        int[] invScale = { 40, 45, 51, 57, 64, 72 };
        static int Log2(int s) => s switch { 4 => 2, 8 => 3, 16 => 4, _ => 5 };

        void Dequant(short[] c, int qp, int size)
        {
            int shift = 8 + Log2(size) - 9;
            long scale = (long)invScale[qp % 6] << (qp / 6);
            long add = 1L << (shift - 1);
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = (short)Math.Clamp(((c[i] * scale) + add) >> shift, -32768, 32767);
            }
        }

        var rng = new Random(7);
        foreach (int size in new[] { 4, 8, 16, 32 })
        {
            foreach (bool dst in new[] { false, true })
            {
                if (dst && size != 4)
                {
                    continue;
                }

                int n = size * size;
                double se = 0;
                const int trials = 30;
                for (int t = 0; t < trials; t++)
                {
                    var resid = new short[n];
                    for (int i = 0; i < n; i++)
                    {
                        resid[i] = (short)rng.Next(-40, 40);
                    }

                    var coeff = new short[n];
                    HevcForwardTransform.Forward(resid, coeff, size, 8, dst);
                    HevcForwardTransform.Quantize(coeff, 12, size, 8, true);
                    Dequant(coeff, 12, size);
                    var rec = new short[n];
                    HevcTransform.InverseTransform(coeff, rec, size, dst, 8);
                    for (int i = 0; i < n; i++)
                    {
                        double d = resid[i] - rec[i];
                        se += d * d;
                    }
                }

                double rmse = Math.Sqrt(se / (n * trials));
                // QP 12 quantization error is ~1 unit; allow generous headroom.
                await Assert.That(rmse).IsLessThan(3.0);
            }
        }
    }

    [Test]
    public async Task ResidualCoding_RoundTrips_Through_Decoder()
    {
        var rng = new Random(99);
        int fail = 0;
        foreach (bool sdh in new[] { false, true })
        {
            foreach (int log2 in new[] { 2, 3, 4, 5 })
            {
                foreach (int scan in new[] { 0, 1, 2 })
                {
                    if (scan != 0 && log2 > 3)
                    {
                        continue;
                    }

                    int n = 1 << log2;
                    const int qp = 27;
                    bool dst = log2 == 2;
                    for (int t = 0; t < 40; t++)
                    {
                        var resid = new short[n * n];
                        for (int i = 0; i < n * n; i++)
                        {
                            resid[i] = (short)rng.Next(-80, 80);
                        }

                        var coeff = new short[n * n];
                        HevcForwardTransform.Forward(resid, coeff, n, 8, dst);
                        var orig = (short[])coeff.Clone();
                        var deltaU = new int[n * n];
                        HevcForwardTransform.Quantize(coeff, qp, n, 8, true, deltaU);
                        if (sdh)
                        {
                            HevcForwardTransform.ApplySignHiding(coeff, orig, deltaU, n, scan);
                        }

                        if (System.Linq.Enumerable.All(coeff, c => c == 0))
                        {
                            continue;
                        }

                        var w = new HevcBitWriter();
                        var enc = new HevcCabacEncoder(w);
                        enc.InitializeContexts(qp, 0);
                        enc.Start();
                        HevcResidualEncoder.Encode(enc, coeff, log2, scan, 0, sdh);
                        enc.EncodeTerminate(1);
                        enc.Finish();
                        w.ByteAlignWithStopBit();

                        var sps = new HevcSequenceParameterSet();
                        var pps = new HevcPictureParameterSet { SignDataHidingEnabled = sdh };
                        var dec = new HevcDecoder();
                        short[] got = dec.TestDecodeResidual(w.ToArray(), sps, pps, log2, scan, 0, qp);
                        for (int i = 0; i < n * n; i++)
                        {
                            if (got[i] != coeff[i])
                            {
                                fail++;
                                break;
                            }
                        }
                    }
                }
            }
        }

        await Assert.That(fail).IsEqualTo(0);
    }
}
