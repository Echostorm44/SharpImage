using SharpImage.Formats.Av1;
namespace SharpImage.Tests.Formats;

/// <summary>The lowbd (16-bit lane) forward transform against the 32-bit reference one: equal coefficients for every tx
/// size up to 32, every type the size allows, over random and extreme 8-bit residuals.</summary>
public sealed class AomFwdLbdTests
{
    [Test]
    public async Task LowbdForwardMatchesReference()
    {
        bool avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;   // without it: the SSE driver against the reference only
        var rng = new Random(11);
        int mismatches = 0, cases = 0;
        for (int txSize = 0; txSize < AomTables.TxSizeWide.Length; txSize++)
        {
            int w = AomTables.TxSizeWide[txSize], h = AomTables.TxSizeHigh[txSize];
            if (w > 32 || h > 32) continue;
            for (int txType = 0; txType < 16; txType++)
            {
                if ((w == 32 || h == 32) && txType != 0 && txType != 9) continue;   // DCT_DCT / IDTX only
                AomEncodeMb.TxTypeKinds(txType, out int hKind, out int vKind, out bool flipUd, out bool flipLr);
                for (int trial = 0; trial < 60; trial++)
                {
                    int stride = w + 8;
                    var diff = new short[stride * h];
                    int mode = trial % 6;
                    for (int i = 0; i < diff.Length; i++)
                        diff[i] = mode switch
                        {
                            0 => (short)rng.Next(-255, 256),
                            1 => (short)(rng.Next(2) == 0 ? -255 : 255),
                            2 => 255,
                            3 => -255,
                            4 => (short)((((i % stride) + (i / stride)) & 1) == 0 ? 255 : -255),
                            _ => (short)rng.Next(-20, 21),
                        };
                    var a = new int[w * h];
                    var b = new int[w * h];
                    Av1FwdTxfmAom.ForwardRawRef(diff, stride, w, h, txSize, hKind, vKind, flipUd, flipLr, a);
                    if (avx2) Av1FwdTxfmAom.ForwardRawLbd(diff, stride, w, h, txSize, hKind, vKind, flipUd, flipLr, b);
                    else a.AsSpan().CopyTo(b);
                    var sse = new int[w * h];
                    Av1FwdTxfmAom.ForwardRawLbdSse(diff, stride, w, h, txSize, hKind, vKind, flipUd, flipLr, sse);
                    cases++;
                    if (!a.AsSpan().SequenceEqual(sse) && mismatches++ < 10) Console.WriteLine($"txSize {txSize} ({w}x{h}) type {txType}: SSE driver differs");
                    if (!a.AsSpan().SequenceEqual(b))
                    {
                        if (mismatches++ < 10)
                        {
                            int k = 0; while (a[k] == b[k]) k++;
                            Console.WriteLine($"txSize {txSize} ({w}x{h}) type {txType} mode {mode}: first diff at {k}: ref {a[k]} lbd {b[k]}");
                        }
                    }
                }
            }
        }
        Console.WriteLine($"{cases} cases, {mismatches} mismatches");
        await Assert.That(mismatches).IsEqualTo(0);
    }
}
