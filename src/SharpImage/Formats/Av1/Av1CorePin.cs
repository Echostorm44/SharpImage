using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Pins encoder worker threads to distinct physical cores. Unpinned, the Windows scheduler often co-schedules the
/// wavefront's workers on SMT siblings of one core (measured on an 8-core / 16-thread part: each worker 23% slower at
/// 2 threads, 54% at 8; pinned to separate cores: no slowdown). Worker k gets the first logical processor of the k-th
/// physical core (faster efficiency class first) within the process affinity; workers past the core count get the
/// cores' other SMT threads, then run unpinned. Windows only (single processor group); elsewhere a no-op.
/// </summary>
internal static class Av1CorePin
{
    private static readonly nint[] Masks = Load();

    /// <summary>Physical cores available for pinning (0 when pinning is unavailable).</summary>
    internal static int Cores { get; private set; }

    /// <summary>Pins the calling thread for worker <paramref name="worker"/>; returns the previous mask for Unpin
    /// (0 = not pinned).</summary>
    internal static nint Pin(int worker)
    {
        if (worker < 0 || worker >= Masks.Length) return 0;
        try { return SetThreadAffinityMask(GetCurrentThread(), Masks[worker]); } catch { return 0; }
    }

    internal static void Unpin(nint previous)
    {
        if (previous == 0) return;
        try { SetThreadAffinityMask(GetCurrentThread(), previous); } catch { }
    }

    private static nint[] Load()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("AV1_NOPIN") == "1") return [];
            if (!GetProcessAffinityMask(GetCurrentProcess(), out nint procMask, out _) || procMask == 0) return [];
            uint len = 0;
            GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, 0, ref len);
            if (len == 0) return [];
            nint buf = Marshal.AllocHGlobal((int)len);
            var cores = new List<(int Eff, List<nint> Threads)>();
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return [];
                for (int off = 0; off < len;)
                {
                    int rel = Marshal.ReadInt32(buf, off), size = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    if (rel == 0)
                    {
                        // PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, Reserved[20], GroupCount, GROUP_AFFINITY[] at +32
                        int eff = Marshal.ReadByte(buf, off + 9), groupCount = Marshal.ReadInt16(buf, off + 30);
                        if (groupCount == 1 && Marshal.ReadInt16(buf, off + 8 + 24 + nint.Size) == 0)
                        {
                            long mask = Marshal.ReadIntPtr(buf, off + 32) & procMask;
                            var threads = new List<nint>();
                            for (int b = 0; b < 64; b++) if ((mask >> b & 1) != 0) threads.Add((nint)1 << b);
                            if (threads.Count > 0) cores.Add((eff, threads));
                        }
                    }
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (cores.Count < 2) return [];
            var order = new List<(int Eff, List<nint> Threads)>(cores);
            order.Sort((a, b) => b.Eff.CompareTo(a.Eff));   // stable enough: higher efficiency class = performance cores
            var masks = new List<nint>();
            for (int t = 0, any = 1; any != 0; t++)
            {
                any = 0;
                foreach (var c in order) if (t < c.Threads.Count) { masks.Add(c.Threads[t]); any = 1; }
            }
            Cores = cores.Count;
            return masks.ToArray();
        }
        catch { return []; }
    }

    [DllImport("kernel32")] private static extern nint SetThreadAffinityMask(nint thread, nint mask);
    [DllImport("kernel32")] private static extern nint GetCurrentThread();
    [DllImport("kernel32")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetProcessAffinityMask(nint process, out nint processMask, out nint systemMask);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);
}
