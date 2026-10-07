using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpImage.Formats;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Mutation fuzzing of the untrusted-input decoders (AVIF / HEIF container + AV1, raw AV1 OBU streams, JPEG). A decode of
// corrupt data may only fail with InvalidDataException / NotSupportedException (the documented "bad input" errors);
// any other exception is a bug, and so is a decode that runs past the time limit. Seed corpora are the committed test
// assets. The quick test runs a fixed, seeded budget in every suite; the probe (SHARPIMAGE_FUZZ="<iterations> <seed>
// <workers> <outDir> [Avif,Obu,Jpeg]") runs longer campaigns and saves every failing input to outDir.
public sealed class DecoderFuzz
{
    internal enum Kind { Avif, Obu, Jpeg }

    internal static IEnumerable<(Kind Kind, byte[] Data, string Name)> Seeds()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        foreach (var f in Directory.GetFiles(assets, "*.*", SearchOption.AllDirectories).Order())
        {
            var len = new FileInfo(f).Length;
            if (len > 256 * 1024) continue;   // keep iterations fast; large seeds add little over their small siblings
            string ext = Path.GetExtension(f).ToLowerInvariant();
            Kind? k = ext switch { ".avif" or ".heic" or ".heif" => Kind.Avif, ".obu" => Kind.Obu, ".jpg" or ".jpeg" => Kind.Jpeg, _ => null };
            if (k is { } kind) yield return (kind, File.ReadAllBytes(f), Path.GetFileName(f));
        }
    }

    internal static void Decode(Kind kind, byte[] data)
    {
        switch (kind)
        {
            case Kind.Avif:
                using (HeifCoder.DecodeSequence(data)) { }
                HeifCoder.DecodeProgressive(data);
                break;
            case Kind.Obu:
                var dec = new Av1Decoder { FrameSizeLimit = AvifDecodeOptions.DefaultImageSizeLimit };
                foreach (var tu in Av1Conformance.ReadObuFile(data, annexB: false))
                    foreach (var (f, _) in dec.DecodeTemporalUnit(tu, 0)) f.Dispose();
                break;
            case Kind.Jpeg:
                JpegCoder.Read(new MemoryStream(data));
                break;
        }
    }

    internal static byte[] Mutate(byte[] src, Random r)
    {
        var d = new List<byte>(src);
        int ops = 1 + r.Next(4);
        for (int o = 0; o < ops && d.Count > 0; o++)
        {
            int pos = r.Next(d.Count);
            switch (r.Next(8))
            {
                case 0: d[pos] ^= (byte)(1 << r.Next(8)); break;                                   // bit flip
                case 1: d[pos] = (byte)r.Next(256); break;                                          // random byte
                case 2: d[pos] = new byte[] { 0, 0xFF, 0x7F, 0x80, 1 }[r.Next(5)]; break;          // interesting byte
                case 3: d.RemoveRange(pos, d.Count - pos); break;                                   // truncate
                case 4: d.RemoveRange(pos, Math.Min(d.Count - pos, 1 + r.Next(64))); break;         // delete a chunk
                case 5:                                                                              // duplicate a chunk
                    int n = Math.Min(d.Count - pos, 1 + r.Next(64));
                    d.InsertRange(r.Next(d.Count), d.GetRange(pos, n));
                    break;
                case 6:                                                                              // interesting 32-bit value
                    if (pos + 4 <= d.Count)
                    {
                        uint v = new uint[] { 0, 0xFFFFFFFF, 0x7FFFFFFF, 0x80000000, 0x10000, 0xFFFF }[r.Next(6)];
                        for (int k = 0; k < 4; k++) d[pos + k] = (byte)(v >> (24 - 8 * k));
                    }
                    break;
                default: for (int k = 0; k < 1 + r.Next(16) && pos + k < d.Count; k++) d[pos + k] = (byte)r.Next(256); break;
            }
        }
        return [.. d];
    }

    // One campaign: iterations mutated inputs per seed (seeded), `workers` in parallel. Returns failure descriptions
    // ("seed-name #i: Type message @frame"); saves each failing input to outDir when given.
    internal static List<string> Run(int iterations, int seed, int workers, string? outDir, TimeSpan timeout, Kind[]? kinds = null)
    {
        var seeds = Seeds().Where(s => kinds == null || kinds.Contains(s.Kind)).ToList();
        var failures = new ConcurrentBag<string>();
        var jobs = seeds.SelectMany((s, si) => Enumerable.Range(0, iterations).Select(i => (s, si, i))).ToList();
        int hung = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = workers }, job =>
        {
            if (Volatile.Read(ref hung) != 0) return;
            var (s, si, i) = job;
            // Not HashCode.Combine: its seed is random per process, which made the "fixed" budget a new one every run.
            var input = Mutate(s.Data, new Random(unchecked((seed * 1_000_003 + si) * 1_000_003 + i)));
            string id = $"{s.Name}#{i}";
            string? saved = null;
            if (outDir != null) File.WriteAllBytes(saved = Path.Combine(outDir, $"inflight_{Environment.CurrentManagedThreadId}.bin"), input);
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { Decode(s.Kind, input); }
                catch (Exception e) { error = e; }
            }, 16 * 1024 * 1024) { IsBackground = true };
            t.Start();
            if (!t.Join(timeout))
            {
                Interlocked.Exchange(ref hung, 1);
                failures.Add($"{id}: HANG > {timeout.TotalSeconds}s");
                if (saved != null) File.Copy(saved, Path.Combine(outDir!, $"hang_{s.Name}_{i}.bin"), true);
                return;
            }
            if (error is null or InvalidDataException or NotSupportedException) return;
            // The first frames inside SharpImage (framework throw helpers skipped), without file paths.
            string top = string.Join(" < ", (error.StackTrace ?? "").Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("at SharpImage.")).Take(3).Select(l => l[3..(l.IndexOf('(') is var k and > 0 ? k : l.Length)]));
            failures.Add($"{id}: {error.GetType().Name} {error.Message.Split('\n')[0]} {top}");
            if (saved != null) File.Copy(saved, Path.Combine(outDir!, $"fail_{s.Name}_{i}_{error.GetType().Name}.bin"), true);
        });
        return [.. failures.OrderBy(f => f)];
    }

    [Test]
    public async Task CorruptInputs_FailOnlyWithInvalidData()
    {
        var failures = Run(iterations: 4, seed: 1, workers: 2, outDir: null, timeout: TimeSpan.FromSeconds(60));
        await Assert.That(string.Join("\n", failures)).IsEqualTo("");
    }

    // Replays one saved input (SHARPIMAGE_FUZZ_ONE="<Avif|Obu|Jpeg> <file>"), writing the full exception next to it.
    [Test, Explicit]
    public void ReplayOne()
    {
        if (Environment.GetEnvironmentVariable("SHARPIMAGE_FUZZ_ONE") is not { } cfg) return;
        int sp = cfg.IndexOf(' ');
        string file = cfg[(sp + 1)..];
        string result;
        try { Decode(Enum.Parse<Kind>(cfg[..sp]), File.ReadAllBytes(file)); result = "ok"; }
        catch (Exception e) { result = e.ToString(); }
        File.WriteAllText(file + ".txt", result);
    }

    [Test, Explicit]
    public async Task Campaign()
    {
        if (Environment.GetEnvironmentVariable("SHARPIMAGE_FUZZ") is not { } cfg) return;   // opt-in
        var a = cfg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Directory.CreateDirectory(a[3]);
        var kinds = a.Length > 4 ? a[4].Split(',').Select(Enum.Parse<Kind>).ToArray() : null;
        var failures = Run(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2]), a[3], TimeSpan.FromSeconds(60), kinds);
        File.WriteAllLines(Path.Combine(a[3], "failures.txt"), failures);
        await Assert.That(failures.Count).IsEqualTo(0);
    }
}
