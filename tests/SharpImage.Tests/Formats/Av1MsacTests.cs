using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Round-trip verification for the AV1 entropy ENCODER (Av1MsacWriter) against the decoder (Av1Msac, dav1d-
// ported). If these pass, the arithmetic coder + carry/finalize are bit-correct — the bedrock for the AVIF
// encoder. Each op is encoded, the stream is finished, then decoded replaying the SAME op sequence.
// Av1Msac is a ref struct (can't cross await), so decode runs synchronously and the verdict is asserted after.
public sealed class Av1MsacTests
{
    private enum OpKind { Bool, Equi, Symbol, Literal }

    private readonly record struct Op(OpKind Kind, uint Val, uint F, int Nsyms, ushort[]? Icdf, int Nbits);

    // A VALID AV1-style inverse CDF (Q15): every symbol gets a probability floor so the encoder's quantized
    // partition can never underflow (real AV1 CDFs are similarly bounded). Probabilities >= 128, sum 32768;
    // icdf[i] = P(symbol > i) * 32768, strictly decreasing, icdf[nsyms-1] = 0.
    private static ushort[] MakeIcdf(Random rng, int nsyms)
    {
        const int floor = 128;
        var probs = new int[nsyms];
        int remaining = 32768 - (floor * nsyms);
        for (int i = 0; i < nsyms; i++)
        {
            probs[i] = floor;
        }

        for (int i = 0; remaining > 0 && i < nsyms; i++)
        {
            int add = i == nsyms - 1 ? remaining : rng.Next(remaining + 1);
            probs[i] += add;
            remaining -= add;
        }

        var icdf = new ushort[nsyms];
        int cum = 0;
        for (int i = 0; i < nsyms; i++)
        {
            cum += probs[i];
            icdf[i] = (ushort)(32768 - cum);
        }

        return icdf;
    }

    // Returns -1 on full round-trip success, else the index of the first op whose decode diverged.
    private static int RunNonAdaptive(int seed)
    {
        var rng = new Random((seed * 7919) + 1);
        var ops = new List<Op>();
        int n = 200 + rng.Next(600);
        for (int i = 0; i < n; i++)
        {
            switch (rng.Next(4))
            {
                case 0:
                    ops.Add(new Op(OpKind.Bool, (uint)rng.Next(2), (uint)(1 + rng.Next(32767)), 0, null, 0));
                    break;
                case 1:
                    ops.Add(new Op(OpKind.Equi, (uint)rng.Next(2), 0, 0, null, 0));
                    break;
                case 2:
                    int nsyms = 2 + rng.Next(14);
                    ushort[] icdf = MakeIcdf(rng, nsyms);
                    ops.Add(new Op(OpKind.Symbol, (uint)rng.Next(nsyms), 0, nsyms, icdf, 0));
                    break;
                default:
                    int nbits = 1 + rng.Next(16);
                    ops.Add(new Op(OpKind.Literal, (uint)rng.Next(1 << nbits), 0, 0, null, nbits));
                    break;
            }
        }

        var w = new Av1MsacWriter();
        foreach (Op op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.Bool: w.EncodeBool(op.Val, op.F); break;
                case OpKind.Equi: w.EncodeBoolEqui(op.Val); break;
                case OpKind.Symbol: w.EncodeSymbol(op.Icdf!, (int)op.Val, op.Nsyms); break;
                case OpKind.Literal: w.EncodeLiteral(op.Val, op.Nbits); break;
            }
        }

        byte[] bytes = w.Finish();

        var r = new Av1Msac(bytes, disableCdfUpdate: true);
        for (int i = 0; i < ops.Count; i++)
        {
            Op op = ops[i];
            uint got;
            switch (op.Kind)
            {
                case OpKind.Bool: got = r.DecodeBool(op.F); break;
                case OpKind.Equi: got = r.DecodeBoolEqui(); break;
                case OpKind.Symbol:
                    var cdf = new ushort[op.Nsyms + 1];
                    op.Icdf!.CopyTo(cdf, 0);
                    got = r.DecodeSymbolAdapt(cdf, op.Nsyms);
                    break;
                default:
                    got = 0;
                    for (int b = op.Nbits - 1; b >= 0; b--)
                    {
                        got |= r.DecodeBoolEqui() << b;
                    }

                    break;
            }

            if (got != op.Val)
            {
                return i;
            }
        }

        return -1;
    }

    // Adaptive: encoder and decoder each keep their own copy of persistent CDFs and adapt identically, so a
    // fixed op stream round-trips only if the adaptation math matches too.
    private static int RunAdaptive(int seed)
    {
        var rng = new Random((seed * 104729) + 3);
        int nCtx = 6;
        var boolCtx = new ushort[nCtx][];
        var symNsyms = new int[nCtx];
        var symCtx = new ushort[nCtx][];
        for (int c = 0; c < nCtx; c++)
        {
            boolCtx[c] = new ushort[] { (ushort)(1 + rng.Next(32766)), 0 };
            symNsyms[c] = 2 + rng.Next(12);
            var ic = MakeIcdf(rng, symNsyms[c]);
            symCtx[c] = new ushort[symNsyms[c] + 1];
            ic.CopyTo(symCtx[c], 0);
        }

        var encBool = Clone(boolCtx);
        var decBool = Clone(boolCtx);
        var encSym = Clone(symCtx);
        var decSym = Clone(symCtx);

        var kinds = new List<(bool IsBool, int Ctx, uint Val)>();
        int n = 400 + rng.Next(800);
        for (int i = 0; i < n; i++)
        {
            bool isBool = rng.Next(2) == 0;
            int ctx = rng.Next(nCtx);
            uint val = isBool ? (uint)rng.Next(2) : (uint)rng.Next(symNsyms[ctx]);
            kinds.Add((isBool, ctx, val));
        }

        var w = new Av1MsacWriter();
        foreach (var (isBool, ctx, val) in kinds)
        {
            if (isBool)
            {
                w.EncodeBoolAdapt(encBool[ctx], val);
            }
            else
            {
                w.EncodeSymbolAdapt(encSym[ctx], (int)val, symNsyms[ctx]);
            }
        }

        byte[] bytes = w.Finish();

        var r = new Av1Msac(bytes, disableCdfUpdate: false);
        for (int i = 0; i < kinds.Count; i++)
        {
            var (isBool, ctx, val) = kinds[i];
            uint got = isBool
                ? r.DecodeBoolAdapt(decBool[ctx])
                : r.DecodeSymbolAdapt(decSym[ctx], symNsyms[ctx]);
            if (got != val)
            {
                return i;
            }
        }

        return -1;
    }

    [Test]
    public async Task Msac_NonAdaptive_RoundTrips()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            await Assert.That(RunNonAdaptive(seed)).IsEqualTo(-1);
        }
    }

    [Test]
    public async Task Msac_Adaptive_RoundTrips()
    {
        for (int seed = 0; seed < 40; seed++)
        {
            await Assert.That(RunAdaptive(seed)).IsEqualTo(-1);
        }
    }

    private static ushort[][] Clone(ushort[][] src)
    {
        var dst = new ushort[src.Length][];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = (ushort[])src[i].Clone();
        }

        return dst;
    }
}
