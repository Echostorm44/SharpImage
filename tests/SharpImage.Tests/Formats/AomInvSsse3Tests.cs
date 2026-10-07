using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

/// <summary>libaom's SSSE3 lowbd inverse transform (every size: what runs without AVX2, the default Native AOT
/// instruction set) against the generic inverse (the normative arithmetic): the same reconstruction for every tx size
/// and type, random sparse coefficients up to random eobs.</summary>
public sealed class AomInvSsse3Tests
{
    [Test]
    public async Task Ssse3Inverse_MatchesGenericInverse_AllSizes()
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported)
        {
            Skip.Test("compares against the SSSE3 kernels: this CPU has no SSSE3");
        }
        var rng = new Random(23);
        int cases = 0, bad = 0;
        for (int txSize = 0; txSize < AomTables.TxSizeWide.Length; txSize++)
        {
            int w = AomTables.TxSizeWide[txSize], h = AomTables.TxSizeHigh[txSize];
            int n = AomEncodeMb.MaxEob(txSize);
            for (int txType = 0; txType < 16; txType++)
            {
                if (w == 64 || h == 64) continue;   // no SSSE3 64-point kernels (the generic inverse takes them)
                if ((w == 32 || h == 32) && txType != 0 && txType != 9) continue;
                var scan = AomEncodeMb.ScanOf(txSize, txType);
                for (int trial = 0; trial < 12; trial++)
                {
                    int eob = trial == 0 ? 1 : trial == 1 ? n : rng.Next(1, n + 1);
                    var coef = new int[n];
                    int amp = trial % 3 == 0 ? 64 : trial % 3 == 1 ? 1024 : 4000;
                    for (int i = 0; i < eob; i++)
                        if (i == eob - 1 || rng.Next(3) == 0) coef[scan[i]] = rng.Next(-amp, amp + 1) | (i == eob - 1 ? 1 : 0);
                    int stride = w + 16;
                    var a = new byte[stride * h];
                    for (int i = 0; i < a.Length; i++) a[i] = (byte)rng.Next(256);
                    var b = (byte[])a.Clone();
                    Av1InvTransform.InvTxfmAdd16(a, stride, coef.AsSpan(), eob - 1, txSize, Av1InvTransform.TxShift[txSize],
                        (Av1TxType)txType, 8, preserveCoeffs: true);
                    unsafe
                    {
                        fixed (int* pi = coef)
                        fixed (byte* po = b)
                            AomInvTxfmLbd.InvTxfm2dAddSsse3(pi, po, stride, txType, txSize, eob);
                    }
                    cases++;
                    if (!a.AsSpan().SequenceEqual(b) && bad++ < 8) Console.WriteLine($"txSize {txSize} ({w}x{h}) type {txType} eob {eob}: differs");
                }
            }
        }
        Console.WriteLine($"ssse3 inverse: {cases} cases, {bad} mismatches");
        await Assert.That(bad).IsEqualTo(0);
    }
}
