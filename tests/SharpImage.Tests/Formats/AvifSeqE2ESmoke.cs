using SharpImage.Image;
using SharpImage.Formats;
namespace SharpImage.Tests.Formats;
// LOCAL (not committed): SE2E_Y4M=<y4m> SE2E_OUT=<avif> SE2E_Q=<quality> SE2E_S=<speed> SE2E_J=<threads> [SE2E_TUNE=psnr|iq|ssim]
public sealed class AvifSeqE2ESmoke
{
    [Test]
    public async Task EncodesSequence()
    {
        ImageSequence seq;
        if (Environment.GetEnvironmentVariable("SE2E_PNGS") is { Length: > 0 } pngs)
        {
            seq = new ImageSequence { Timescale = 30 };
            foreach (var f in pngs.Split(';'))
            {
                var fr = PngCoder.Read(f);
                fr.DurationTicks = 1;
                seq.AddFrame(fr);
            }
        }
        else seq = Y4mCoder.ReadSequence(File.ReadAllBytes(Environment.GetEnvironmentVariable("SE2E_Y4M")!));
        var o = new AvifEncodeOptions
        {
            Speed = int.Parse(Environment.GetEnvironmentVariable("SE2E_S") ?? "6"),
            MaxThreads = int.Parse(Environment.GetEnvironmentVariable("SE2E_J") ?? "1"),
        };
        if (Environment.GetEnvironmentVariable("SE2E_Q") is { Length: > 0 } q) o.Quality = int.Parse(q);
        o.Tune = Environment.GetEnvironmentVariable("SE2E_TUNE") switch { "psnr" => AvifTune.Psnr, "iq" => AvifTune.Iq, "ssim" => AvifTune.Ssim, _ => null };
        if (Environment.GetEnvironmentVariable("SE2E_KF") is { Length: > 0 } kf) o.KeyframeInterval = int.Parse(kf);
        if (Environment.GetEnvironmentVariable("SE2E_CT") is { Length: > 0 } ct)
            o.CreationTime = o.ModificationTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(ct));
        var avif = HeifCoder.EncodeAvifSequence(seq, o);
        File.WriteAllBytes(Environment.GetEnvironmentVariable("SE2E_OUT")!, avif);
        await Assert.That(avif.Length).IsGreaterThan(0);
    }
}
