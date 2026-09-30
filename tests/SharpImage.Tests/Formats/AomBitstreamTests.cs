using System.Security.Cryptography;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>The libaom-port bitstream writer: the range coder against SharpImage's decoder and the older writer, and
/// whole packets against libaom 3.14.1's own bytes.</summary>
public sealed class AomBitstreamTests
{
    /// <summary>Random adaptive symbols (dav1d-layout CDFs), adaptive bools, raw bits and literals: AomWriter's bytes
    /// decode with Av1Msac and equal Av1MsacWriter's for the same operations (termination included).</summary>
    private static (int FirstBadDecode, bool SameBytes) RunWriter(int seed)
    {
        var rng = new Random(seed * 7907 + 11);
        const int nCtx = 8;
        var counts = new int[nCtx];
        var cdfs = new ushort[nCtx][];
        for (int c = 0; c < nCtx; c++)
        {
            int n = 2 + rng.Next(15);   // 2..16 symbols
            counts[c] = n;
            // dav1d layout: n - 1 decreasing inverse CDF values (each symbol >= 64 / 32768), then the counter
            var cdf = new ushort[n];
            int remaining = 32768 - 64 * n, cum = 0;
            for (int i = 0; i < n - 1; i++)
            {
                int p = 64 + rng.Next(remaining / 2 + 1);
                remaining -= p - 64;
                cum += p;
                cdf[i] = (ushort)(32768 - cum);
            }
            cdfs[c] = cdf;
        }
        var ops = new List<(int Kind, int Ctx, int Val, int Bits)>();
        int count = 300 + rng.Next(3000);
        for (int i = 0; i < count; i++)
        {
            int kind = rng.Next(3);
            int ctx = rng.Next(nCtx);
            if (kind == 0) ops.Add((0, ctx, rng.Next(counts[ctx]), 0));
            else if (kind == 1) ops.Add((1, ctx, rng.Next(2), 0));
            else { int bits = 1 + rng.Next(8); ops.Add((2, 0, rng.Next(1 << bits), bits)); }
        }

        var aw = new AomWriter();
        var mw = new Av1MsacWriter();
        var aCdfs = cdfs.Select(a => (ushort[])a.Clone()).ToArray();
        var mCdfs = cdfs.Select(a => (ushort[])a.Clone()).ToArray();
        foreach (var (kind, ctx, val, bits) in ops)
        {
            if (kind == 0)
            {
                aw.WriteSymbol(val, aCdfs[ctx], counts[ctx]);
                if (counts[ctx] == 2) mw.EncodeBoolAdapt(mCdfs[ctx], (uint)val);
                else mw.EncodeSymbolAdapt(mCdfs[ctx], val, counts[ctx] - 1);
            }
            else if (kind == 1)
            {
                aw.WriteBit(val);
                mw.EncodeBoolEqui((uint)val);
            }
            else
            {
                aw.WriteLiteral(val, bits);
                mw.EncodeLiteral((uint)val, bits);
            }
        }
        byte[] a = aw.Finish(), m = mw.Finish();

        var dCdfs = cdfs.Select(x => (ushort[])x.Clone()).ToArray();
        var r = new Av1Msac(a);
        for (int i = 0; i < ops.Count; i++)
        {
            var (kind, ctx, val, bits) = ops[i];
            int got = kind == 0
                ? (int)(counts[ctx] == 2 ? r.DecodeBoolAdapt(dCdfs[ctx]) : r.DecodeSymbolAdapt(dCdfs[ctx], counts[ctx] - 1))
                : kind == 1 ? (int)r.DecodeBoolEqui() : (int)r.DecodeBools(bits);
            if (got != val) return (i, a.AsSpan().SequenceEqual(m));
        }
        return (-1, a.AsSpan().SequenceEqual(m));
    }

    [Test]
    public async Task AomWriter_RoundTrips_And_MatchesMsacWriter()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var (bad, same) = RunWriter(seed);
            await Assert.That(bad).IsEqualTo(-1);
            await Assert.That(same).IsTrue();
        }
    }

    /// <summary>The synthetic 200x150 4:2:0 frame of AomEncoderSmoke (checkerboard ramp with noise).</summary>
    private static AomEncodeInput SyntheticInput(int speed, int qindex)
    {
        const int w = 200, h = 150, cw = 100, ch = 75;
        byte[] y = new byte[w * h], u = new byte[cw * ch], v = new byte[cw * ch];
        var rng = new Random(3);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                y[r * w + c] = (byte)Math.Clamp((r * 2 + c) / 2 + (((r / 16) + (c / 16)) & 1) * 60 + rng.Next(-8, 9), 0, 255);
        for (int i = 0; i < u.Length; i++) { u[i] = (byte)(100 + (i % 37)); v[i] = (byte)(140 - (i % 23)); }
        return new AomEncodeInput { Width = w, Height = h, Planes = new[] { y, u, v }, Strides = new[] { w, cw, cw }, BaseQindex = qindex, Speed = speed };
    }

    // SHA-256 of libaom 3.14.1's packet (aom_codec_get_cx_data) for the synthetic frame, encoded the way avifenc
    // configures libaom (ALL_INTRA, AOM_Q, cq-level 28 / 10 -> qindex 112 / 40, cpu-used, full range,
    // AV1E_SET_SKIP_POSTPROC_FILTERING, tune PSNR, one thread).
    [Test]
    [Arguments(6, 112, "a805c49e85577b0cf3a2472712c56018929ee2481c14a4456190cc1566a90b8c", 1851)]
    [Arguments(2, 40, "6551ade5cdef533b84b3dc32f04674ed61ef1a45f29cf5840e49abc18831215c", 10919)]
    public async Task PackFrame_MatchesLibaomPacket(int speed, int qindex, string sha256, int length)
    {
        var (cpi, x) = AomEncoder.EncodeFrame(SyntheticInput(speed, qindex));
        AomEncoder.RunPostFilter(cpi, x);
        byte[] packet = AomBitstream.PackFrame(cpi);
        await Assert.That(packet.Length).IsEqualTo(length);
        await Assert.That(Convert.ToHexString(SHA256.HashData(packet)).ToLowerInvariant()).IsEqualTo(sha256);
    }
}
