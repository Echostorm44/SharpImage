using System;
using System.IO;
using System.Linq;
using SharpImage.Formats;

namespace SharpImage.Tests.Formats;

// JpegCoder.Encode is libjpeg-turbo 3.1's compressor as cjpeg drives it: the output must equal cjpeg's byte for byte.
// The references are cjpeg 3.1.4 runs over a 33x47 crop (TestAssets/jpeg_cjpeg); the full conformance sweep (images x
// switch sets: 8 / 12-bit, sequential / progressive / scan scripts, Huffman / arithmetic, lossless at 2..16 bits) ran
// against cjpeg with the cjpeg probe.
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
        "arith" => new() { Arithmetic = true, RestartRows = 1 },
        "arith_prog" => new() { Arithmetic = true, Progressive = true, Quality = 90 },
        "lossless6" => new() { LosslessPredictor = 6, LosslessPointTransform = 1 },
        "lossless12_rst" => new() { LosslessPredictor = 7, Precision = 12, RestartRows = 2 },
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
    [Arguments("arith")]
    [Arguments("arith_prog")]
    [Arguments("lossless6")]
    [Arguments("lossless12_rst")]
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
    [Arguments(JpegColorSpace.Cmyk)]
    [Arguments(JpegColorSpace.Ycck)]
    public async Task CmykRoundTripsAsInkAmounts(JpegColorSpace space)
    {
        // CMYK frames hold ink amounts; the file stores them Adobe-inverted (checked byte-exact against libjpeg with the
        // cmykjpeg probe) and the decoder flips them back.
        var src = new SharpImage.Image.ImageFrame();
        src.Initialize(24, 16, SharpImage.Core.ColorspaceType.CMYK, false);
        for (int y = 0; y < 16; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < 24; x++)
                for (int k = 0; k < 4; k++) row[x * 4 + k] = (ushort)((x * 9 + y * 5 + k * 60) % 256 * 257);
        }
        using var back = JpegCoder.Read(new MemoryStream(JpegCoder.Encode(src, new JpegEncodeOptions { ColorSpace = space, Quality = 100, SamplingFactors = [(1, 1)] })));
        await Assert.That(back.Colorspace).IsEqualTo(SharpImage.Core.ColorspaceType.CMYK);
        double err = 0;
        for (int y = 0; y < 16; y++)
        {
            var a = src.GetPixelRow(y); var b = back.GetPixelRow(y);
            for (int i = 0; i < 24 * 4; i++) err += Math.Abs(a[i] - b[i]) / 257.0;
        }
        await Assert.That(err / (24 * 16 * 4)).IsLessThan(1.5);
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
