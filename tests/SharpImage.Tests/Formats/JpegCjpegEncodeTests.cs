using System;
using System.IO;
using System.Linq;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// JpegCoder.Encode is libjpeg-turbo 3.1's compressor as cjpeg drives it: the output must equal cjpeg's byte for byte.
// The references are cjpeg 3.1.4 runs over a 33x47 crop (TestAssets/jpeg_cjpeg); the full conformance sweep (images x
// switch sets, 8 / 12-bit, sequential / progressive / scan scripts) ran against cjpeg with the cjpeg probe.
public sealed class JpegCjpegEncodeTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "TestAssets", "jpeg_cjpeg");

    private static JpegEncodeOptions Options(string name) => name switch
    {
        "default" => new(),
        "q10_opt_rst" => new() { Quality = 10, OptimizeCoding = true, RestartRows = 1 },
        "grey_smooth" => new() { ColorSpace = JpegColorSpace.Grayscale, Smoothing = 40 },
        "rgb_float" => new() { ColorSpace = JpegColorSpace.Rgb, Dct = JpegDctMethod.Float, Quality = 90 },
        "h2v1_fast" => new() { SamplingFactors = [(2, 1)], Dct = JpegDctMethod.IntegerFast },
        "q1_nobaseline" => new() { Quality = 1 },
        "prog" => new() { Progressive = true },
        "prog12" => new() { Progressive = true, Precision = 12 },
        "seq12" => new() { Precision = 12, Quality = 95 },
        "scans_deep" => new()
        {
            Scans =
            [
                new([0, 1, 2], 0, 0, 0, 2), new([0], 1, 9, 0, 3), new([0], 10, 63, 0, 2), new([1], 1, 63, 0, 1), new([2], 1, 63, 0, 1),
                new([0], 1, 9, 3, 2), new([0], 1, 63, 2, 1), new([0], 1, 63, 1, 0), new([1], 1, 63, 1, 0), new([2], 1, 63, 1, 0),
                new([0, 1, 2], 0, 0, 2, 1), new([0, 1, 2], 0, 0, 1, 0),
            ],
        },
        _ => throw new ArgumentException(name),
    };

    [Test]
    [Arguments("default")]
    [Arguments("q10_opt_rst")]
    [Arguments("grey_smooth")]
    [Arguments("rgb_float")]
    [Arguments("h2v1_fast")]
    [Arguments("q1_nobaseline")]
    [Arguments("prog")]
    [Arguments("prog12")]
    [Arguments("seq12")]
    [Arguments("scans_deep")]
    public async Task MatchesCjpeg(string name)
    {
        var src = FormatRegistry.Read(Path.Combine(Dir, "src.ppm"));
        byte[] ours = JpegCoder.Encode(src, Options(name));
        byte[] reference = File.ReadAllBytes(Path.Combine(Dir, name + ".jpg"));
        await Assert.That(ours.AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task RoundTripsThroughTheDecoder()
    {
        var src = FormatRegistry.Read(Path.Combine(Dir, "src.ppm"));
        using var back = JpegCoder.Read(new MemoryStream(JpegCoder.Encode(src, new JpegEncodeOptions { Quality = 95, SamplingFactors = [(1, 1)] })));
        await Assert.That((back.Columns, back.Rows)).IsEqualTo((src.Columns, src.Rows));
        double err = 0;
        for (int y = 0; y < (int)src.Rows; y++)
        {
            var a = src.GetPixelRow(y); var b = back.GetPixelRow(y);
            for (int i = 0; i < (int)src.Columns * 3; i++) err += Math.Abs(a[i] - b[i]) / 257.0;
        }
        await Assert.That(err / (src.Columns * src.Rows * 3)).IsLessThan(3.0);
    }

    [Test]
    public async Task InvalidScriptsAndTablesFailLikeLibjpeg()
    {
        var src = FormatRegistry.Read(Path.Combine(Dir, "src.ppm"));
        // AC before DC, and a refinement that skips a bit: jcmaster.c validate_script rejects both
        await Assert.That(() => JpegCoder.Encode(src, new JpegEncodeOptions { Scans = [new([0], 1, 63, 0, 0), new([0, 1, 2], 0, 0, 0, 0)] }))
            .Throws<ArgumentException>();
        await Assert.That(() => JpegCoder.Encode(src, new JpegEncodeOptions { Scans = [new([0, 1, 2], 0, 0, 0, 2), new([0, 1, 2], 0, 0, 1, 0)] }))
            .Throws<ArgumentException>();
        // a component using quantization table 2, which cjpeg's -quality never defines
        await Assert.That(() => JpegCoder.Encode(src, new JpegEncodeOptions { QuantTableSlots = [0, 1, 2] })).Throws<InvalidOperationException>();
    }
}
