using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Verifies the AV1 codestream writer (Av1ObuWriter) produces headers the decoder's parser reads back with the
// intended fields. The parser (Av1ObuParser, dav1d-ported) is the oracle: write → parse → assert.
public sealed class Av1ObuWriterTests
{
    [Test]
    public async Task SequenceHeader_Monochrome_RoundTrips()
    {
        var cfg = new Av1ObuWriter.SeqConfig(64, 64, monochrome: true);
        byte[] payload = Av1ObuWriter.WriteSequenceHeaderPayload(cfg);

        var hdr = new Av1DecoderSequenceHeader();
        Av1ObuParser.ParseResult result = Av1ObuParser.ParseSequenceHeader(hdr, payload);

        await Assert.That(result).IsEqualTo(Av1ObuParser.ParseResult.Ok);
        await Assert.That((int)hdr.Profile).IsEqualTo(0);
        await Assert.That(hdr.StillPicture).IsTrue();
        await Assert.That(hdr.ReducedStillPictureHeader).IsTrue();
        await Assert.That(hdr.MaxWidth).IsEqualTo(64);
        await Assert.That(hdr.MaxHeight).IsEqualTo(64);
        await Assert.That(hdr.Monochrome).IsTrue();
        await Assert.That(hdr.Sb128).IsFalse();
        await Assert.That(hdr.Layout).IsEqualTo(Av1PixelLayout.I400);
    }

    [Test]
    public async Task SequenceHeaderObu_WrapsAndParsesViaHeader()
    {
        var cfg = new Av1ObuWriter.SeqConfig(128, 96, monochrome: true);
        byte[] payload = Av1ObuWriter.WriteSequenceHeaderPayload(cfg);
        byte[] obu = Av1ObuWriter.WrapObu(Av1ObuType.SequenceHeader, payload);

        bool ok = Av1HeaderParser.TryParseObuHeader(obu, out Av1ObuHeader oh);
        await Assert.That(ok).IsTrue();
        await Assert.That(oh.Type).IsEqualTo(Av1ObuType.SequenceHeader);
        await Assert.That(oh.HasSize).IsTrue();
        await Assert.That(oh.PayloadSize).IsEqualTo(payload.Length);

        var hdr = new Av1DecoderSequenceHeader();
        var slice = obu.AsSpan(oh.HeaderSize, oh.PayloadSize);
        Av1ObuParser.ParseResult result = Av1ObuParser.ParseSequenceHeader(hdr, slice.ToArray());
        await Assert.That(result).IsEqualTo(Av1ObuParser.ParseResult.Ok);
        await Assert.That(hdr.MaxWidth).IsEqualTo(128);
        await Assert.That(hdr.MaxHeight).IsEqualTo(96);
    }

    [Test]
    public async Task FrameHeader_ReducedStillKey_RoundTrips()
    {
        var seqCfg = new Av1ObuWriter.SeqConfig(64, 64, monochrome: true);
        var seq = new Av1DecoderSequenceHeader();
        await Assert.That(Av1ObuParser.ParseSequenceHeader(seq, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg)))
            .IsEqualTo(Av1ObuParser.ParseResult.Ok);

        const int baseQ = 40;
        byte[] fh = Av1ObuWriter.WriteFrameHeaderPayload(baseQ, isObuFrame: false);

        var ctx = new Av1DecoderContext();
        var frame = new Av1DecoderFrameHeader();
        Av1ObuParser.ParseResult result = Av1ObuParser.ParseFrameHeader(
            frame, seq, ctx.RefFrames, fh, out int consumed, isObuFrame: false);

        await Assert.That(result).IsEqualTo(Av1ObuParser.ParseResult.Ok);
        await Assert.That(frame.FrameType).IsEqualTo(Av1FrameType.Key);
        await Assert.That(frame.ShowFrame).IsTrue();
        await Assert.That(frame.DisableCdfUpdate).IsTrue();
        await Assert.That(frame.CodedWidth).IsEqualTo(64);
        await Assert.That(frame.Height).IsEqualTo(64);
        await Assert.That((int)frame.QuantBaseQIdx).IsEqualTo(baseQ);
        await Assert.That(frame.AllLossless).IsFalse();
        await Assert.That((int)frame.TileCols).IsEqualTo(1);
        await Assert.That((int)frame.TileRows).IsEqualTo(1);
        await Assert.That(frame.ReducedTxSet).IsTrue();
        // Every payload byte must be consumed (trailing bit + alignment land exactly at the end).
        await Assert.That(consumed).IsEqualTo(fh.Length);
    }

    [Test]
    public async Task FrameHeader_ObuFrame_ByteAlignedNoTrailing()
    {
        var seqCfg = new Av1ObuWriter.SeqConfig(64, 64, monochrome: true);
        var seq = new Av1DecoderSequenceHeader();
        Av1ObuParser.ParseSequenceHeader(seq, Av1ObuWriter.WriteSequenceHeaderPayload(seqCfg));

        // OBU_FRAME header: append a sentinel "tile" byte; parser must consume exactly the header and point at it.
        byte[] fh = Av1ObuWriter.WriteFrameHeaderPayload(40, isObuFrame: true);
        var combined = new byte[fh.Length + 1];
        fh.CopyTo(combined, 0);
        combined[^1] = 0xAB;

        var ctx = new Av1DecoderContext();
        var frame = new Av1DecoderFrameHeader();
        Av1ObuParser.ParseResult result = Av1ObuParser.ParseFrameHeader(
            frame, seq, ctx.RefFrames, combined, out int consumed, isObuFrame: true);

        await Assert.That(result).IsEqualTo(Av1ObuParser.ParseResult.Ok);
        await Assert.That(consumed).IsEqualTo(fh.Length);
        await Assert.That(combined[consumed]).IsEqualTo((byte)0xAB);
    }
}
