using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

// Twin of the lowbd inverse transform port (AomInvTxfmLbd: av1_lowbd_inv_txfm2d_add_avx2 and its SSSE3 paths) against
// libaom 3.14.1's RTCD-dispatched av1_inv_txfm_add through aomtwin_inv (tests/native/aomtwin: twin_inv.c, build.sh;
// CI builds it). Point SHARPIMAGE_AOMTWIN_INV at aomtwin_inv.dll / .so; without it the test reports Skipped.
[NotInParallel]
public sealed class AomInvTxfmTwinTests
{
    private const string EnvVar = "SHARPIMAGE_AOMTWIN_INV";
    private static readonly string? DllPath = AomTwinNative.PathFromEnv(EnvVar);

    private static bool loaded;

    private static void Load()
    {
        AomTwinNative.SkipUnless(DllPath != null, EnvVar);
        if (loaded)
        {
            return;
        }
        AomTwinNative.Register("aomtwin_inv", DllPath!);
        Native.twin_init();
        loaded = true;
    }

    private static unsafe class Native
    {
        private const string D = "aomtwin_inv";
        [DllImport(D)] public static extern void twin_init();
        [DllImport(D)] public static extern int twin_inv_is_avx2();
        [DllImport(D)] public static extern void twin_inv_txfm_add(int* dqcoeff, int ncoef, byte* dst, int stride, int txType,
            int txSize, int eob);
    }

    private static readonly string[] SizeNames = { "4x4", "8x8", "16x16", "32x32", "64x64", "4x8", "8x4", "8x16", "16x8",
        "16x32", "32x16", "32x64", "64x32", "4x16", "16x4", "8x32", "32x8", "16x64", "64x16" };

    /// <summary>The tx types some tx set allows at this size: all 16 up to 16 x 16, DCT_DCT / IDTX at 32, DCT_DCT at 64.</summary>
    private static int[] TypesFor(int txSize)
    {
        int m = Math.Max(TxSizeWide[txSize], TxSizeHigh[txSize]);
        return m <= 16 ? Enumerable.Range(0, 16).ToArray() : m == 32 ? new[] { 0, 9 } : new[] { 0 };
    }

    private static int Coef(Random rng, int kind)
    {
        int s = rng.Next(2) == 0 ? -1 : 1;
        return kind switch
        {
            0 => s * rng.Next(0, 4),
            1 => s * rng.Next(0, 64),
            2 => s * rng.Next(0, 1024),
            3 => s * rng.Next(0, 8192),
            4 => s * rng.Next(0, 32768),
            5 => s * rng.Next(0, 1 << 20),                       // beyond int16: the loads' packs saturate
            6 => rng.Next(4) == 0 ? s * rng.Next(20000, 1 << 18) : s * rng.Next(0, 16),
            7 => s * (32767 + rng.Next(-2, 3)),                   // the int16 edge
            _ => rng.Next(3) == 0 ? s * rng.Next(0, 4096) : 0,
        };
    }

    [Test]
    public async Task InvTxfm_AllSizesTypesEobs_MatchLibaom()
    {
        Load();
        await Assert.That(Native.twin_inv_is_avx2()).IsEqualTo(1);
        await Assert.That(AomInvTxfmLbd.Supported).IsTrue();
        var rng = new Random(20261001);
        int cases = 0, mismatches = 0, changed = 0, oldDiffers = 0;
        string firstBad = "";
        for (int txSize = 0; txSize < 19; txSize++)
        {
            int w = TxSizeWide[txSize], h = TxSizeHigh[txSize];
            int maxEob = AomEncodeMb.MaxEob(txSize);
            foreach (int txType in TypesFor(txSize))
            {
                ushort[] scan = AomEncodeMb.ScanOf(txSize, txType);
                for (int iter = 0; iter < 120; iter++)
                {
                    int eob = iter switch
                    {
                        0 => 1,
                        1 => maxEob,
                        2 => 2,
                        _ => rng.Next(4) switch
                        {
                            0 => rng.Next(1, Math.Min(maxEob, 16) + 1),
                            1 => rng.Next(1, Math.Min(maxEob, 80) + 1),
                            _ => rng.Next(1, maxEob + 1),
                        },
                    };
                    int kind = rng.Next(9);
                    int[] coef = new int[maxEob];
                    for (int i = 0; i < eob; i++) coef[scan[i]] = Coef(rng, kind);
                    if (coef[scan[eob - 1]] == 0) coef[scan[eob - 1]] = rng.Next(2) == 0 ? -1 - rng.Next(100) : 1 + rng.Next(100);
                    int stride = w + rng.Next(0, 24);
                    var dst = new byte[stride * h + 32];
                    int dk = rng.Next(4);
                    for (int i = 0; i < dst.Length; i++)
                        dst[i] = dk switch { 0 => (byte)rng.Next(256), 1 => (byte)(rng.Next(2) * 255), 2 => (byte)rng.Next(120, 136), _ => (byte)rng.Next(256) };
                    var mine = (byte[])dst.Clone();
                    var theirs = (byte[])dst.Clone();
                    var coefCopy = (int[])coef.Clone();
                    AomEncodeMb.InverseTransformBlock(coefCopy, 0, txType, txSize, mine, 0, stride, eob);
                    unsafe
                    {
                        fixed (int* pc = coef)
                        fixed (byte* pd = theirs)
                            Native.twin_inv_txfm_add(pc, maxEob, pd, stride, txType, txSize, eob);
                    }
                    cases++;
                    if (!theirs.AsSpan().SequenceEqual(dst)) changed++;
                    var old = (byte[])dst.Clone();
                    Av1InvTransform.InvTxfmAdd16(old, stride, ((int[])coef.Clone()).AsSpan(0, maxEob), eob - 1, txSize,
                        Av1InvTransform.TxShift[txSize], (Av1TxType)txType, 8, preserveCoeffs: true);
                    if (!old.AsSpan().SequenceEqual(theirs)) oldDiffers++;
                    bool same = mine.AsSpan().SequenceEqual(theirs) && coefCopy.AsSpan().SequenceEqual(coef);
                    if (!same)
                    {
                        if (mismatches == 0)
                        {
                            int at = 0;
                            while (at < mine.Length && mine[at] == theirs[at]) at++;
                            firstBad = $"{SizeNames[txSize]} type {txType} eob {eob} kind {kind}: first diff at ({at % stride},{at / stride}) " +
                                (at < mine.Length ? $"ours {mine[at]} libaom {theirs[at]}" : "coefficients modified");
                        }
                        mismatches++;
                    }
                }
            }
        }
        Console.WriteLine($"inverse transform twin: {cases} cases ({changed} changing dst; the 32-bit decoder transform differs on {oldDiffers}), {mismatches} mismatches {firstBad}");
        await Assert.That(firstBad).IsEqualTo("");
        await Assert.That(mismatches).IsEqualTo(0);
    }
}
