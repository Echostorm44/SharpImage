using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Dev harness for the encoder speed / efficiency trade-off (opt-in: SPEEDEXP=<configs file> <out file>). Each config
// line "name Knob=value,Knob=value" sets Av1StillImageEncoder's internal static knobs (reset to their defaults
// before every config), encodes each 8-bit 4:2:0 source listed in the configs file's "img <path> <w> <h>" lines at
// base_q_idx 40/100/160/220, decodes with our decoder and writes "config image qidx bytes psnrY psnrU psnrV seconds".
public sealed class Av1SpeedExperiment
{
    [Test, NotInParallel]
    public void Run()
    {
        if (Environment.GetEnvironmentVariable("SPEEDEXP") is not { } env) return;
        var a = env.Split(' ', 2);
        var lines = File.ReadAllLines(a[0]).Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).ToList();
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(a[0]))!;
        var images = lines.Where(l => l.StartsWith("img ")).Select(l => l.Split(' '))
            .Select(t => (Path: Path.GetFullPath(t[1], baseDir), W: int.Parse(t[2]), H: int.Parse(t[3]))).ToList();
        var configs = lines.Where(l => !l.StartsWith("img ")).ToList();
        var enc = typeof(Av1StillImageEncoder);
        bool Knob(Type t) => t == typeof(bool) || t == typeof(int) || t == typeof(double) || t == typeof(long);
        var knobs = new Dictionary<string, (Type Type, Func<object?> Get, Action<object?> Set)>();
        foreach (var f in enc.GetFields(BindingFlags.NonPublic | BindingFlags.Static).Where(f => !f.IsInitOnly && !f.IsLiteral && Knob(f.FieldType)))
            knobs[f.Name] = (f.FieldType, () => f.GetValue(null), v => f.SetValue(null, v));
        foreach (var pr in enc.GetProperties(BindingFlags.NonPublic | BindingFlags.Static).Where(pr => pr.CanWrite && Knob(pr.PropertyType)))
            knobs[pr.Name] = (pr.PropertyType, () => pr.GetValue(null), v => pr.SetValue(null, v));
        var defaults = knobs.ToDictionary(k => k.Key, k => k.Value.Get());
        using var outW = new StreamWriter(a[1], append: true) { AutoFlush = true };
        foreach (var cfg in configs)
        {
            foreach (var (k, v) in defaults) knobs[k].Set(v);
            var parts = cfg.Split(' ', 2);
            if (parts.Length > 1)
                foreach (var kv in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = kv.Split('=');
                    var kn = knobs[p[0]];
                    kn.Set(Convert.ChangeType(p[1], kn.Type, System.Globalization.CultureInfo.InvariantCulture));
                }
            foreach (var img in images)
            {
                byte[] all = File.ReadAllBytes(img.Path);
                int w = img.W, h = img.H, cw = (w + 1) / 2, ch = (h + 1) / 2;
                var y = all.AsSpan(0, w * h).ToArray();
                var u = all.AsSpan(w * h, cw * ch).ToArray();
                var v = all.AsSpan(w * h + cw * ch, cw * ch).ToArray();
                foreach (int q in new[] { 40, 100, 160, 220 })
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] avif = Av1StillImageEncoder.EncodeAvifColorMultiSb(y, u, v, w, h, q);
                    double secs = sw.Elapsed.TotalSeconds;
                    var c = SharpImage.Formats.HeifContainer.Parse(avif);
                    using var f = new Av1Decoder().Decode(c.ItemData(c.PrimaryId)!, 0, true)!;
                    double Psnr(ReadOnlySpan<byte> src, ReadOnlySpan<byte> dec, int stride, int pw, int ph)
                    {
                        double sse = 0;
                        for (int yy = 0; yy < ph; yy++)
                            for (int xx = 0; xx < pw; xx++) { int d = src[yy * pw + xx] - dec[yy * stride + xx]; sse += d * d; }
                        double mse = sse / (pw * ph);
                        return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255 / mse);
                    }
                    double py = Psnr(y, f.YPlane.Span, f.YStride, w, h), pu = Psnr(u, f.UPlane.Span, f.UStride, cw, ch), pv = Psnr(v, f.VPlane.Span, f.VStride, cw, ch);
                    outW.WriteLine($"{parts[0]} {Path.GetFileNameWithoutExtension(img.Path)} {q} {avif.Length} {py:F4} {pu:F4} {pv:F4} {secs:F3}");
                }
            }
        }
        foreach (var (k, v) in defaults) knobs[k].Set(v);
    }
}
