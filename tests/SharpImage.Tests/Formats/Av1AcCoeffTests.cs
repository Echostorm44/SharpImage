using SharpImage.Formats.Av1;

namespace SharpImage.Tests.Formats;

// Verifies the full AC coefficient-coding path (Av1CoeffEncode) end-to-end: a 64x64 luma block carrying several
// hand-crafted quantized coefficients (DC + low-frequency AC, including a base-range/HiTok level and a negative
// sign) encodes to a stream our Av1Decoder decodes without error. The strong cross-check (identical pixels in
// ffmpeg/libdav1d) runs from the dump test below; here we assert our decoder produces a non-flat 64x64 plane.
public sealed class Av1AcCoeffTests
{
    // rc index within the TX_64X64 coeff grid: rc = (x << 5) | y (shift = slh+2 = 5, mask = 31).
    private static int Rc(int x, int y) => (x << 5) | y;

    private static int[] SampleCoeffs()
    {
        var cf = new int[Av1Tables.Scans[4].Length]; // TX_64X64
        cf[Rc(0, 0)] = 6;    // DC
        cf[Rc(0, 1)] = -3;   // AC, HiTok (|level| >= 3), negative
        cf[Rc(1, 0)] = 2;    // AC, base token
        cf[Rc(1, 1)] = 1;    // AC, base token
        cf[Rc(0, 2)] = -1;   // AC, negative
        return cf;
    }

    [Test]
    public async Task AcCoeffs_64x64_DecodeWithoutError()
    {
        int[] cf = SampleCoeffs();
        byte[] stream = Av1StillImageEncoder.EncodeMonochromeWithCoeffs(64, 64, baseQIdx: 160, coeffs: cf);

        var decoder = new Av1Decoder();
        decoder.Initialize(default);
        DecodedVideoFrame? frame = decoder.Decode(stream, 0, isKeyframe: true);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Width).IsEqualTo(64);
        await Assert.That(frame.Height).IsEqualTo(64);

        // With several nonzero AC coefficients the reconstructed plane must vary (not a flat DC plane).
        var y = frame.YPlane.Span;
        int stride = frame.YStride;
        int min = 255, max = 0;
        for (int row = 0; row < 64; row++)
        {
            for (int col = 0; col < 64; col++)
            {
                int v = y[row * stride + col];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        await Assert.That(max).IsGreaterThan(min); // non-flat
    }
}
