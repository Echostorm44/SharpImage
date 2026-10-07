using System;
using System.IO;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Core;
using TUnit.Core;

namespace SharpImage.Tests.Formats;

// Scratch rate-distortion benchmark for the AV1 intra encoder vs libaom. Not a correctness gate —
// run explicitly with the treenode filter. Encodes bench_src.png at a sweep of qp values, decodes
// back, and reports bytes + RGB-PSNR to a file the harness can diff against ffmpeg/libaom output.
// Developer harness (needs local data / a trigger): [Explicit] keeps it out of the normal and CI runs; select it by name.
[Explicit]
public sealed class Av1EncodeBench
{
    const string Dir = @"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad";

    static double PsnrRgb(ImageFrame a, ImageFrame b, int w, int h)
    {
        double sse = 0; int ch = Math.Min(a.NumberOfChannels, 3);
        for (long y = 0; y < h; y++)
            for (long x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    int pa = (a.GetPixelChannel(x, y, c) * 255 + 32767) / 65535;
                    int pb = (b.GetPixelChannel(x, y, c) * 255 + 32767) / 65535;
                    int d = pa - pb; sse += d * (double)d;
                }
        double mse = sse / (w * (double)h * ch);
        return mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }

    // Feeds an identical I420 source (bench_src.yuv) straight into the AV1 encoder, bypassing RGB↔YUV
    // conversion, so the codec can be compared to libaom on the same planes. Writes .avif per qp.
    [Test]
    public void BenchYuv()
    {
        string yuvPath = Path.Combine(Dir, "bench_src.yuv");
        if (!File.Exists(yuvPath)) return; // dev-only benchmark; source is generated in the scratchpad by hand
        const int W = 256, H = 256; int cw = W / 2, ch = H / 2;
        int ysz = W * H, csz = cw * ch;
        byte[] all = File.ReadAllBytes(yuvPath);
        var y = all.AsSpan(0, ysz).ToArray();
        var u = all.AsSpan(ysz, csz).ToArray();
        var v = all.AsSpan(ysz + csz, csz).ToArray();
        var sb = new System.Text.StringBuilder();
        foreach (int qp in new[] { 1, 2, 4, 8, 14, 20, 28, 36, 44 })
        {
            int baseQIdx = Math.Clamp((int)Math.Round(Math.Clamp(qp, 0, 51) * (255.0 / 51.0)), 4, 255);
            byte[] avif = SharpImage.Formats.Av1.Av1StillImageEncoder.EncodeAvifColorMultiSb(y, u, v, W, H, baseQIdx);
            File.WriteAllBytes(Path.Combine(Dir, $"benchyuv_our_q{qp}.avif"), avif);
            sb.AppendLine($"qp={qp} baseQIdx={baseQIdx} bytes={avif.Length}");
        }
        File.WriteAllText(Path.Combine(Dir, "benchyuv_our.txt"), sb.ToString());
    }

    [Test]
    public void Bench()
    {
        string srcPath = Environment.GetEnvironmentVariable("BENCH_SRC") ?? Path.Combine(Dir, "bench_src.png");
        if (!File.Exists(srcPath)) return; // dev-only benchmark; source generated in the scratchpad by hand
        using var src = PngCoder.Read(srcPath);
        int w = (int)src.Columns, h = (int)src.Rows;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"src {w}x{h}");
        foreach (int qp in new[] { 1, 2, 4, 8, 14, 20, 28, 36, 44 })
        {
            byte[] avif;
            try { avif = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = qp, Speed = 0 }); }   // the full search
            catch (Exception ex) { sb.AppendLine($"qp={qp} ENCODE-THREW {ex.GetType().Name}: {ex.Message}"); continue; }
            using var dec = HeifCoder.Decode(avif);
            double psnr = PsnrRgb(src, dec, w, h);
            double bpp = avif.Length * 8.0 / (w * (double)h);
            sb.AppendLine($"qp={qp} bytes={avif.Length} bpp={bpp:F4} psnr={psnr:F2}");
            File.WriteAllBytes(Path.Combine(Dir, $"bench_our_q{qp}.avif"), avif);
        }
        File.WriteAllText(Path.Combine(Dir, "bench_our.txt"), sb.ToString());
    }
}
