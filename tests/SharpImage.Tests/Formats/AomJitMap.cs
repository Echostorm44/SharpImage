using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
namespace SharpImage.Tests.Formats;

/// <summary>Profiling aid for AomEncoderSmoke (AOM_SMOKE_JITMAP=&lt;file&gt;): records the runtime's MethodLoadVerbose
/// events (start address, size, name, tier of every jitted method version) through an in-process EventListener, so an
/// external native sampler (suspend + read the encode thread's RIP) can attribute its samples to the managed code
/// exactly. AOM_SMOKE_TIDFILE=&lt;file&gt;: the encode thread's OS id, written when the encode starts.</summary>
internal sealed class AomJitMap : EventListener
{
    private readonly List<string> _lines = new();

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    internal static void WriteThreadId(string path) => File.WriteAllText(path, GetCurrentThreadId().ToString());

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)(0x10 | (Environment.GetEnvironmentVariable("AOM_SMOKE_ALLOCS") != null ? 0x1 : 0)));   // Jit (+ GC: AllocationTick)
    }

    private readonly Dictionary<string, long> _allocs = new();

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName != null && e.EventName.StartsWith("GCAllocationTick") && e.Payload != null && e.PayloadNames != null)
        {
            int it = e.PayloadNames.IndexOf("TypeName"), ia = e.PayloadNames.IndexOf("AllocationAmount64"), ik = e.PayloadNames.IndexOf("AllocationKind"),
                isz = e.PayloadNames.IndexOf("ObjectSize");
            if (it >= 0 && ia >= 0)
                lock (_allocs)
                {
                    string kind = ik >= 0 && Convert.ToInt32(e.Payload[ik]) == 1 ? "LOH " : "";
                    string osz = kind.Length > 0 && isz >= 0 ? $" (object {Convert.ToInt64(e.Payload[isz])})" : "";
                    string k = kind + (string)e.Payload[it]! + osz;
                    _allocs[k] = _allocs.GetValueOrDefault(k) + Convert.ToInt64(e.Payload[ia]);
                }
            return;
        }
        if (e.EventName == null || !e.EventName.StartsWith("MethodLoadVerbose") || e.Payload == null) return;
        int iStart = e.PayloadNames!.IndexOf("MethodStartAddress"), iSize = e.PayloadNames.IndexOf("MethodSize"),
            iNs = e.PayloadNames.IndexOf("MethodNamespace"), iName = e.PayloadNames.IndexOf("MethodName"),
            iTier = e.PayloadNames.IndexOf("OptimizationTier");
        if (iStart < 0 || iSize < 0) return;
        ulong start = Convert.ToUInt64(e.Payload[iStart]);
        uint size = Convert.ToUInt32(e.Payload[iSize]);
        string name = $"{e.Payload[iNs]}.{e.Payload[iName]} [{(iTier >= 0 ? e.Payload[iTier] : "?")}]";
        lock (_lines) _lines.Add($"{start:x} {size:x} {name}");
    }

    internal void Save(string path)
    {
        lock (_lines)
        {
            // the loaded native modules too (coreclr's helpers, the JIT, CoreLib's precompiled code)
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                _lines.Add($"{(ulong)m.BaseAddress:x} {m.ModuleMemorySize:x} module:{m.ModuleName}");
            File.WriteAllLines(path, _lines);
            if (_allocs.Count > 0)
                File.WriteAllLines(path + ".allocs", _allocs.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value / 1048576.0:F1} MB  {kv.Key}"));
        }
    }
}
