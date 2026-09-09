using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// End-to-end step-2 verification: the minimal AV1 still-image encoder (Av1StillImageEncoder) produces a
// codestream that our Av1Decoder decodes to a flat DC-predicted monochrome plane. This exercises OBU framing,
// both uncompressed headers, and MSAC tile symbol coding together against the real decoder.
public sealed class Av1StillImageEncoderTests
{
    [Test]
    public async Task FlatMonochrome_64x64_DecodesToDcPlane()
    {
        byte[] stream = Av1StillImageEncoder.EncodeFlatMonochrome(64, 64);

        var decoder = new Av1Decoder();
        decoder.Initialize(default);
        DecodedVideoFrame? frame = decoder.Decode(stream, 0, isKeyframe: true);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Width).IsEqualTo(64);
        await Assert.That(frame.Height).IsEqualTo(64);

        // DC prediction with no available neighbours ⇒ 1<<(bitdepth-1) = 128 for every luma sample.
        var y = frame.YPlane.Span;
        int stride = frame.YStride;
        int mismatches = 0;
        int firstVal = y[0];
        for (int row = 0; row < 64; row++)
        {
            for (int col = 0; col < 64; col++)
            {
                if (y[row * stride + col] != 128)
                {
                    mismatches++;
                }
            }
        }

        await Assert.That(firstVal).IsEqualTo(128);
        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    [Arguments(200, 2, false)]
    [Arguments(200, 2, true)]
    [Arguments(200, 1, false)]
    public async Task DcResidual_64x64_DecodesToUniformNon128(int baseQ, int dcLevel, bool neg)
    {
        byte[] stream = Av1StillImageEncoder.EncodeFlatMonochrome(64, 64, baseQ, dcLevel, neg);

        var decoder = new Av1Decoder();
        decoder.Initialize(default);
        DecodedVideoFrame? frame = decoder.Decode(stream, 0, isKeyframe: true);

        await Assert.That(frame).IsNotNull();
        var y = frame!.YPlane.Span;
        int stride = frame.YStride;
        int v0 = y[0];
        int mismatches = 0;
        for (int row = 0; row < 64; row++)
        {
            for (int col = 0; col < 64; col++)
            {
                if (y[row * stride + col] != v0)
                {
                    mismatches++;
                }
            }
        }

        System.Console.WriteLine($"[DC-RESIDUAL] baseQ={baseQ} dcLevel={dcLevel} neg={neg} => value={v0}");
        await Assert.That(mismatches).IsEqualTo(0);      // uniform plane
        await Assert.That(v0).IsNotEqualTo(128);          // residual actually applied
        if (neg)
        {
            await Assert.That(v0).IsLessThan(128);
        }
        else
        {
            await Assert.That(v0).IsGreaterThan(128);
        }
    }
}
