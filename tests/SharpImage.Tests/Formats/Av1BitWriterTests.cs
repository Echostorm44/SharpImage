using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Round-trip verification for the uncompressed-header bit WRITER (Av1BitWriter) against the reader
// (Av1GetBits). Each primitive must invert exactly, or every OBU header built on it will be malformed.
public sealed class Av1BitWriterTests
{
    private enum K { Bits, Bool, Signed, Uvlc, Uniform, Uleb, Align }

    private readonly record struct Op(K Kind, uint Val, int N, uint Max, int Signed);

    private static int RoundTrip(int seed)
    {
        var rng = new Random((seed * 2654435761u).GetHashCode());
        var ops = new List<Op>();
        int n = 100 + rng.Next(400);
        for (int i = 0; i < n; i++)
        {
            switch (rng.Next(7))
            {
                case 0:
                    int nb = 1 + rng.Next(32);
                    uint mask = nb == 32 ? uint.MaxValue : (1u << nb) - 1;
                    ops.Add(new Op(K.Bits, (uint)(((ulong)(uint)rng.Next() ) & mask), nb, 0, 0));
                    break;
                case 1:
                    ops.Add(new Op(K.Bool, (uint)rng.Next(2), 0, 0, 0));
                    break;
                case 2:
                    // su(n): two's complement in n bits → range [-2^(n-1), 2^(n-1)-1].
                    int sn = 2 + rng.Next(15);
                    int half = 1 << (sn - 1);
                    int sv = rng.Next(-half, half);
                    ops.Add(new Op(K.Signed, 0, sn, 0, sv));
                    break;
                case 3:
                    ops.Add(new Op(K.Uvlc, (uint)rng.Next(0, 1 << 20), 0, 0, 0));
                    break;
                case 4:
                    uint max = (uint)(2 + rng.Next(1000));
                    ops.Add(new Op(K.Uniform, (uint)rng.Next((int)max), 0, max, 0));
                    break;
                case 5:
                    ops.Add(new Op(K.Uleb, (uint)rng.Next(), 0, 0, 0));
                    break;
                default:
                    ops.Add(new Op(K.Align, 0, 0, 0, 0));
                    break;
            }
        }

        var w = new Av1BitWriter();
        foreach (Op op in ops)
        {
            switch (op.Kind)
            {
                case K.Bits: w.PutBits(op.Val, op.N); break;
                case K.Bool: w.PutBool(op.Val != 0); break;
                case K.Signed: w.PutSignedBits(op.Signed, op.N); break;
                case K.Uvlc: w.PutUvlc(op.Val); break;
                case K.Uniform: w.PutUniform(op.Val, op.Max); break;
                case K.Uleb: w.ByteAlign(); w.PutUleb128(op.Val); break;
                case K.Align: w.ByteAlign(); break;
            }
        }

        byte[] bytes = w.ToArray();
        var r = new Av1GetBits(bytes);
        for (int i = 0; i < ops.Count; i++)
        {
            Op op = ops[i];
            switch (op.Kind)
            {
                case K.Bits:
                    if (r.GetBits(op.N) != op.Val) return i;
                    break;
                case K.Bool:
                    if ((r.GetBool() ? 1u : 0u) != op.Val) return i;
                    break;
                case K.Signed:
                    if (r.GetSignedBits(op.N) != op.Signed) return i;
                    break;
                case K.Uvlc:
                    if (r.GetVlc() != op.Val) return i;
                    break;
                case K.Uniform:
                    if (r.GetUniform(op.Max) != op.Val) return i;
                    break;
                case K.Uleb:
                    r.ByteAlign();
                    if (r.GetUleb128() != op.Val) return i;
                    break;
                case K.Align:
                    r.ByteAlign();
                    break;
            }
        }

        return -1;
    }

    [Test]
    public async Task BitWriter_RoundTrips()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            await Assert.That(RoundTrip(seed)).IsEqualTo(-1);
        }
    }
}
