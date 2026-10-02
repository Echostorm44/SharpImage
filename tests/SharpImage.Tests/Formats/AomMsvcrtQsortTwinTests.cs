using System;
using System.Runtime.InteropServices;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// AomPalette.MsvcrtQsort against the real msvcrt.dll qsort (the C library libaom links under mingw-w64): element
// order after sorting tagged keys with many ties, for the shortsort range and the quicksort path.
public sealed partial class AomMsvcrtQsortTwinTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KE { public int Key, Tag; }

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void qsort(void* b, nuint num, nuint width, delegate* unmanaged[Cdecl]<KE*, KE*, int> comp);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int CompDesc(KE* a, KE* b) => a->Key < b->Key ? 1 : a->Key > b->Key ? -1 : 0;

    [Test]
    public async Task MsvcrtQsort_MatchesMsvcrtDll()
    {
        int mismatches = CountMismatches();
        await Assert.That(mismatches).IsEqualTo(0);
    }

    private static unsafe int CountMismatches()
    {
        var rng = new Random(1234);
        int mismatches = 0;
        for (int trial = 0; trial < 4000; trial++)
        {
            int n = 2 + rng.Next(80);
            int keyRange = 1 + rng.Next(6);
            var a = new KE[n];
            for (int i = 0; i < n; i++) a[i] = new KE { Key = rng.Next(keyRange), Tag = i };
            var b = (KE[])a.Clone();
            fixed (KE* p = a) qsort(p, (nuint)n, (nuint)sizeof(KE), &CompDesc);
            AomPalette.MsvcrtQsort<KE>(b, (in KE x, in KE y) => x.Key < y.Key ? 1 : x.Key > y.Key ? -1 : 0);
            for (int i = 0; i < n; i++) if (a[i].Tag != b[i].Tag) { mismatches++; break; }
        }
        return mismatches;
    }
}
