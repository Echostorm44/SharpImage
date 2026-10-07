using System;
using System.IO;
using SharpImage.Formats;
using SharpImage.Formats.Av1;
using TUnit.Core;

// Dev-only: per-phase bit accounting. Decodes a set of .avif files (ours + libaom's) with Av1Msac.AcctOn and
// reports how many BYTES each stream spends on partition / mode / skip / tx / filter / luma-coef / chroma-coef.
// Works on any valid AV1 stream, so it profiles libaom's bit split too. Trigger file bitprof.txt; consumed.
// Developer harness (needs local data / a trigger): [Explicit] keeps it out of the normal and CI runs; select it by name.
[Explicit]
public sealed class Av1BitProfile
{
    const string Corpus = @"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad\corpus";
    static readonly string[] Names = { "partition", "mode", "skip", "tx", "filter", "luma-coef", "chroma-coef", "other",
        "ymode", "yangle", "uvmode", "cfl", "uvangle", "palette", "intrabc", "?" };

    [Test, NotInParallel]
    public void Profile()
    {
        if (!Directory.Exists(Corpus)) return;
        string trig = Path.Combine(Corpus, "bitprof.txt");
        if (!File.Exists(trig)) return;
        var files = File.ReadAllLines(trig);   // each line: label|path-to-avif
        File.Delete(trig);
        var sb = new System.Text.StringBuilder();
        Av1Msac.AcctOn = true;
        try
        {
            foreach (var line in files)
            {
                var parts = line.Split('|');
                if (parts.Length != 2 || !File.Exists(parts[1])) continue;
                Array.Clear(Av1Msac.PhaseBits, 0, Av1Msac.PhaseBits.Length);
                Array.Clear(Av1Msac.ModeHist);
                Av1Msac.Phase = 7;
                using var img = HeifCoder.Decode(File.ReadAllBytes(parts[1]));
                double total = 0; foreach (var b in Av1Msac.PhaseBits) total += b;
                sb.Append($"{parts[0],-22} totalCoded={total / 8:F0}B  ");
                for (int i = 0; i < 15; i++) if (Av1Msac.PhaseBits[i] > 0) sb.Append($"{Names[i]}={Av1Msac.PhaseBits[i] / 8:F0} ");
                sb.AppendLine();
                string[] ym = { "DC", "V", "H", "D45", "D135", "D113", "D157", "D203", "D67", "SM", "SMV", "SMH", "PAETH", "CFL" };
                sb.Append("    y:");
                for (int i = 0; i < 13; i++) sb.Append($" {ym[i]}={Av1Msac.ModeHist[i]}");
                sb.Append("  uv:");
                for (int i = 0; i < 14; i++) sb.Append($" {ym[i]}={Av1Msac.ModeHist[16 + i]}");
                sb.AppendLine($"  palY={Av1Msac.ModeHist[32]} palUV={Av1Msac.ModeHist[33]}");
            }
        }
        finally { Av1Msac.AcctOn = false; }
        File.WriteAllText(Path.Combine(Corpus, "bitprof.out"), sb.ToString());
    }
}
