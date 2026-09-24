using System;
using System.Collections.Generic;
using System.IO;
using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// The public stateless header peek (Av1HeaderParser) against the decoder on libaom's test vectors: the first frame is
// a shown key frame of the decoded size, and exactly one shown frame header (or show_existing_frame) per decoded frame.
public sealed class Av1HeaderParserTests
{
    public static IEnumerable<string> Vectors() =>
    [
        "av1-1-b8-02-allintra.ivf", "av1-1-b8-04-cdfupdate.ivf", "av1-1-b8-05-mv.ivf", "av1-1-b10-24-monochrome.ivf",
        "av1-1-b10-23-film_grain-50.ivf", "av1-1-b8-01-size-16x18.ivf", "av1-1-b8-01-size-66x34.ivf",
    ];

    private static (bool AllParsed, Av1FrameHeader First, int Shown, int Decoded, (int, int) FirstSize) Scan(string vector)
    {
        var tus = Av1Conformance.ReadIvf(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", "av1_vectors", vector)));
        Av1SequenceHeader seq = default;
        bool haveSeq = false, allParsed = true;
        int shown = 0, decoded = 0;
        Av1FrameHeader? first = null;
        (int, int)? firstSize = null;
        var dec = new Av1Decoder();
        for (int i = 0; i < tus.Count; i++)
        {
            ReadOnlySpan<byte> tu = tus[i];
            int pos = 0;
            while (pos < tu.Length)
            {
                if (!Av1HeaderParser.TryParseObuHeader(tu[pos..], out var oh)) { allParsed = false; break; }
                var payload = tu.Slice(pos + oh.HeaderSize, oh.PayloadSize);
                if (oh.Type == Av1ObuType.SequenceHeader)
                    haveSeq = Av1HeaderParser.TryParseSequenceHeader(payload, out seq);
                else if (oh.Type is Av1ObuType.FrameHeader or Av1ObuType.Frame && haveSeq)
                {
                    if (!Av1HeaderParser.TryParseFrameHeader(payload, seq, out var fh)) allParsed = false;
                    first ??= fh;
                    if (fh.ShowFrame) shown++;
                }
                pos += oh.HeaderSize + oh.PayloadSize;
            }
            foreach (var (f, _) in dec.DecodeTemporalUnit(tus[i], i))
            {
                firstSize ??= (f.Width, f.Height);
                decoded++;
                f.Dispose();
            }
        }
        return (allParsed && haveSeq, first!.Value, shown, decoded, firstSize!.Value);
    }

    [Test]
    [MethodDataSource(nameof(Vectors))]
    public async Task FrameHeaders_MatchDecoder(string vector)
    {
        var (allParsed, first, shown, decoded, firstSize) = Scan(vector);
        await Assert.That(allParsed).IsTrue();
        await Assert.That(first.FrameType).IsEqualTo(Av1FrameType.Key);
        await Assert.That(first.ShowFrame).IsTrue();
        await Assert.That(first.RefreshFrameFlags).IsEqualTo((byte)0xFF);
        await Assert.That((first.Width, first.Height)).IsEqualTo(firstSize);
        await Assert.That(shown).IsEqualTo(decoded);
    }
}
