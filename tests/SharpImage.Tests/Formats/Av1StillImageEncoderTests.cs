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
}
