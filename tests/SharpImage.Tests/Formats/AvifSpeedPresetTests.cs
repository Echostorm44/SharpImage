using System;
using System.Linq;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

// AvifEncodeOptions.Speed 0..10 (avifenc -s): every preset produces a valid file of sane quality, and the presets
// above the default really search less (7..9 each change the encode — they once all fell back to speed 6; 10 codes as
// 9, as libavif clamps cpu-used to libaom's 0..9).
public sealed class AvifSpeedPresetTests
{
    private static double Psnr(ImageFrame a, ImageFrame b)
    {
        double sse = 0; long n = 0;
        for (int y = 0; y < (int)a.Rows; y++)
        {
            var ra = a.GetPixelRow(y); var rb = b.GetPixelRow(y);
            for (int x = 0; x < (int)a.Columns; x++)
                for (int c = 0; c < 3; c++)
                {
                    int d = (ra[x * a.NumberOfChannels + c] >> 8) - (rb[x * b.NumberOfChannels + c] >> 8);
                    sse += d * d; n++;
                }
        }
        return 10 * Math.Log10(255.0 * 255 * n / Math.Max(sse, 1e-9));
    }

    [Test]
    public async Task EverySpeedEncodesAndHigherSpeedsDiffer()
    {
        var src = FormatRegistry.Read(System.IO.Path.Combine(AppContext.BaseDirectory, "TestAssets", "peppers.jpg"));
        var outputs = new byte[11][];
        for (int speed = 0; speed <= 10; speed++)
        {
            outputs[speed] = HeifCoder.EncodeAvif(src, new AvifEncodeOptions { Qp = 20, Speed = speed });
            using var back = HeifCoder.Decode(outputs[speed]);
            await Assert.That(back.Columns).IsEqualTo(src.Columns);
            await Assert.That(Psnr(src, back)).IsGreaterThan(30.0);
        }
        await Assert.That(outputs[10].SequenceEqual(outputs[9])).IsTrue();
        var same = Enumerable.Range(7, 3).Where(sp => outputs[sp].SequenceEqual(outputs[sp - 1])).Select(sp => $"{sp - 1}={sp}").ToList();
        await Assert.That(string.Join(",", same)).IsEqualTo("");
    }
}
