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
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);   // JitKeyword: MethodLoadVerbose
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
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
        }
    }
}
