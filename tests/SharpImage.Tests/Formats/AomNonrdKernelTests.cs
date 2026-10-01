using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;

/// <summary>The vector low-precision kernels of the non-RD intra search (AomNonrdPickMode: Hadamard lp 8x8 / 16x16,
/// av1_quantize_lp, aom_satd_lp, av1_block_error_lp) against their scalar references, over random and extreme
/// 8-bit residuals and quantizers.</summary>
public sealed class AomNonrdKernelTests
{
    [Test]
    public async Task VectorKernelsMatchScalar()
    {
        var rng = new Random(7);
        string? fail = null;
        for (int trial = 0; trial < 4000 && fail == null; trial++)
        {
            int stride = 16 + rng.Next(0, 3) * 8;
            var diff = new short[stride * 16];
            int mode = trial % 4;
            for (int i = 0; i < diff.Length; i++)
                diff[i] = mode switch
                {
                    0 => (short)rng.Next(-255, 256),
                    1 => (short)(rng.Next(2) == 0 ? -255 : 255),
                    2 => (short)rng.Next(-8, 9),
                    _ => (short)(i % 3 == 0 ? 255 : -255),
                };
            int n = trial % 3 == 0 ? 64 : trial % 3 == 1 ? 256 : 16;
            var a = new short[256];
            var b = new short[256];
            if (n == 64)
            {
                AomNonrdPickMode.HadamardLp8x8(diff, stride, a);
                AomNonrdPickMode.HadamardLp8x8Scalar(diff, stride, b);
            }
            else if (n == 256)
            {
                AomNonrdPickMode.HadamardLp16x16(diff, stride, a);
                AomNonrdPickMode.HadamardLp16x16Scalar(diff, stride, b);
            }
            else
            {
                AomNonrdPickMode.Fdct4x4Lp(diff, stride, a);
                AomNonrdPickMode.Fdct4x4Lp(diff, stride, b);
            }
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) { fail = $"hadamard n={n} trial {trial} i {i}: {a[i]} != {b[i]}"; break; }
            if (fail != null) break;

            short q0 = (short)rng.Next(1, 32767), q1 = (short)rng.Next(1, 32767);
            short r0 = (short)rng.Next(0, 2000), r1 = (short)rng.Next(0, 2000);
            short d0 = (short)rng.Next(4, 1400), d1 = (short)rng.Next(4, 1800);
            var iscan = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Select(v => (short)v).ToArray();
            var qa = new short[256]; var qb = new short[256]; var da = new short[256]; var db = new short[256];
            int ea = AomNonrdPickMode.QuantizeLp(a, n, r0, r1, q0, q1, qa, da, d0, d1, iscan);
            int eb = AomNonrdPickMode.QuantizeLpScalar(a, n, r0, r1, q0, q1, qb, db, d0, d1, iscan);
            if (ea != eb) { fail = $"quantize eob trial {trial}: {ea} != {eb}"; break; }
            for (int i = 0; i < n; i++)
                if (qa[i] != qb[i] || da[i] != db[i]) { fail = $"quantize trial {trial} i {i}"; break; }
            if (fail != null) break;
            if (AomNonrdPickMode.SatdLp(qa, n) != AomNonrdPickMode.SatdLpScalar(qa, n)) { fail = $"satd trial {trial}"; break; }
            if (AomNonrdPickMode.BlockErrorLp(a, da, n) != AomNonrdPickMode.BlockErrorLpScalar(a, da, n)) { fail = $"error trial {trial}"; break; }
        }
        await Assert.That(fail).IsNull();
    }
}
