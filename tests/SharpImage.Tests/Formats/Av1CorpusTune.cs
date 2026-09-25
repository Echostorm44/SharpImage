using System;
using System.IO;
using System.Linq;
using SharpImage.Formats.Av1;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// Dev-only lambda-tuning harness. Reads one encoder config from env vars, encodes every 256x256 yuv420p in the
// corpus dir across a qp sweep, and writes the resulting .avif files + a byte manifest for an external
// BD-rate comparison. No-ops when the corpus dir is absent. Drive the sweep from bash (one config per run).
public sealed class Av1CorpusTune
{
    const string Corpus = @"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad\corpus";
    const int W = 256, H = 256;

    [Test]
    public void Tune()
    {
        // Runs ONLY when a config file is present (the sweep driver writes one per run); the config is consumed
        // (deleted) so the normal test suite — which never writes it — skips this multi-minute encode sweep.
        // Config line: "label rdlk rdoq crdoq dz" (the test host does not inherit shell env vars).
        if (!Directory.Exists(Corpus)) return;
        string cfgPath = Path.Combine(Corpus, "config.txt");
        if (!File.Exists(cfgPath)) return;
        var p = File.ReadAllText(cfgPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        File.Delete(cfgPath);
        if (p.Where(t => !t.Contains((char)61)).Count() < 5) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string label = p[0];
        double rdlk = double.Parse(p[1], ci), rdoq = double.Parse(p[2], ci), crdoq = double.Parse(p[3], ci), dz = double.Parse(p[4], ci);
        // Any Av1EncodeSpeed field can be set with a trailing "Field=Value" token (ablations / sweeps).
        var speedTok = p.FirstOrDefault(t => t.StartsWith("Speed="));
        var sp = speedTok != null ? Av1EncodeSpeed.ForSpeed(int.Parse(speedTok[6..], ci)) : new Av1EncodeSpeed();
        Av1StillImageEncoder.t_speed = sp;
        foreach (var tok in p)
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0 || tok.StartsWith("Speed=")) continue;
            var f = typeof(Av1EncodeSpeed).GetField(tok[..eq]) ?? throw new ArgumentException("unknown speed field " + tok);
            f.SetValue(sp, Convert.ChangeType(tok[(eq + 1)..], f.FieldType, ci));
        }
        p = p.Where(t => !t.Contains('=')).ToArray();
        Av1StillImageEncoder.RdLambdaK = rdlk;
        Av1StillImageEncoder.RdoqLambdaScale = rdoq;
        Av1StillImageEncoder.ChromaRdoqLambdaScale = crdoq;
        Av1FwdTransform.DeadzoneBias = dz;
        if (p.Length >= 6) Av1StillImageEncoder.UseColorTxDepth = p[5] == "1"; // optional tx-depth toggle
        if (p.Length >= 7) Av1StillImageEncoder.UseDeblockSearch = p[6] == "1"; // optional deblock-search toggle
        if (p.Length >= 8) Av1StillImageEncoder.UsePalette = p[7] == "1";       // optional palette toggle
        if (p.Length >= 9) Av1StillImageEncoder.RdModeCandidates = int.Parse(p[8], ci); // optional mode-search breadth
        if (p.Length >= 10) Av1StillImageEncoder.UseFilterIntra = p[9] == "1";          // optional filter-intra toggle
        if (p.Length >= 11) Av1StillImageEncoder.UseSub8Partition = p[10] == "1";       // optional sub-8x8 partition toggle
        if (p.Length >= 12) Av1StillImageEncoder.EarlyTermBits = double.Parse(p[11], ci); // optional partition early-term threshold

        int cw = W / 2, ch = H / 2, ysz = W * H, csz = cw * ch;
        string outDir = Path.Combine(Corpus, "out", label);
        Directory.CreateDirectory(outDir);
        var manifest = new System.Text.StringBuilder();
        int[] qps = { 4, 10, 18, 28, 40 };
        foreach (var path in Directory.GetFiles(Corpus, "*.yuv"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            byte[] all = File.ReadAllBytes(path);
            var y = all.AsSpan(0, ysz).ToArray();
            var u = all.AsSpan(ysz, csz).ToArray();
            var v = all.AsSpan(ysz + csz, csz).ToArray();
            foreach (int qp in qps)
            {
                int baseQIdx = Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);
                byte[] avif = Av1StillImageEncoder.EncodeAvifColorMultiSb(y, u, v, W, H, baseQIdx);
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_q{qp}.avif"), avif);
                manifest.AppendLine($"{name} {qp} {avif.Length}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "bytes.txt"), manifest.ToString());
    }
}
