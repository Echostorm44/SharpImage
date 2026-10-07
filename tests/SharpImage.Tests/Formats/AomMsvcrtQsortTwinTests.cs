using System;
using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// AomPalette.MsvcrtQsort against the real msvcrt.dll qsort (the C library libaom links under mingw-w64): element
// order after sorting tagged keys with many ties, for the shortsort range and the quicksort path. msvcrt.dll exists
// only on Windows, so its orderings for the fixed-seed inputs are also pinned as a digest (captured from msvcrt.dll,
// and re-checked against it on every Windows run): the port is verified against msvcrt's behaviour on every OS.
public sealed partial class AomMsvcrtQsortTwinTests
{
    // FNV-1a over the tag order msvcrt.dll's qsort leaves for each of the Trials inputs.
    private const string MsvcrtDigest = "50305b93184c721f";
    private const int Trials = 4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct KE { public int Key, Tag; }

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void qsort(void* b, nuint num, nuint width, delegate* unmanaged[Cdecl]<KE*, KE*, int> comp);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int CompDesc(KE* a, KE* b) => a->Key < b->Key ? 1 : a->Key > b->Key ? -1 : 0;

    [Test]
    public async Task MsvcrtQsort_MatchesMsvcrtDigest()
    {
        await Assert.That(Digest(useMsvcrt: false)).IsEqualTo(MsvcrtDigest);
    }

    [Test]
    public async Task MsvcrtQsort_MatchesMsvcrtDll()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("msvcrt.dll exists only on Windows; MsvcrtQsort_MatchesMsvcrtDigest covers the port elsewhere");
        }
        await Assert.That(CountMismatches()).IsEqualTo(0);
        await Assert.That(Digest(useMsvcrt: true)).IsEqualTo(MsvcrtDigest);
    }

    private static KE[] Input(Random rng)
    {
        int n = 2 + rng.Next(80);
        int keyRange = 1 + rng.Next(6);
        var a = new KE[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = new KE { Key = rng.Next(keyRange), Tag = i };
        }
        return a;
    }

    private static unsafe void SortMsvcrt(KE[] a)
    {
        fixed (KE* p = a)
        {
            qsort(p, (nuint)a.Length, (nuint)sizeof(KE), &CompDesc);
        }
    }

    private static void SortPort(KE[] a) =>
        AomPalette.MsvcrtQsort<KE>(a, (in KE x, in KE y) => x.Key < y.Key ? 1 : x.Key > y.Key ? -1 : 0);

    private static string Digest(bool useMsvcrt)
    {
        var rng = new Random(1234);
        ulong h = 1469598103934665603UL;
        for (int trial = 0; trial < Trials; trial++)
        {
            var a = Input(rng);
            if (useMsvcrt)
            {
                SortMsvcrt(a);
            }
            else
            {
                SortPort(a);
            }
            foreach (var e in a)
            {
                h = (h ^ (uint)e.Tag) * 1099511628211UL;
            }
            h = (h ^ 0xFFFFFFFFu) * 1099511628211UL;
        }
        return h.ToString("x16");
    }

    private static int CountMismatches()
    {
        var rng = new Random(1234);
        int mismatches = 0;
        for (int trial = 0; trial < Trials; trial++)
        {
            var a = Input(rng);
            var b = (KE[])a.Clone();
            SortMsvcrt(a);
            SortPort(b);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Tag != b[i].Tag)
                {
                    mismatches++;
                    break;
                }
            }
        }
        return mismatches;
    }
}
