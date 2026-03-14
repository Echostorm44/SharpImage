using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Animation;

/// <summary>
/// Tests for APNG (Animated PNG) read/write operations.
/// </summary>
public class ApngTests
{
    private static ImageFrame CreateSolidFrame(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * 4;
                row[offset] = r;
                row[offset + 1] = g;
                row[offset + 2] = b;
                row[offset + 3] = Quantum.MaxValue;
            }
        }
        return frame;
    }

    [Test]
    public async Task WriteAndReadSequence_RoundTrips_ThreeFrames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_roundtrip_{Guid.NewGuid()}.png");
        try
        {
            var frames = new[]
            {
                CreateSolidFrame(64, 64, Quantum.MaxValue, 0, 0), // Red
                CreateSolidFrame(64, 64, 0, Quantum.MaxValue, 0), // Green
                CreateSolidFrame(64, 64, 0, 0, Quantum.MaxValue), // Blue
            };
            int[] delays = [100, 200, 150];

            PngCoder.WriteSequence(frames, path, delays, loopCount: 0);
            await Assert.That(File.Exists(path)).IsTrue();

            var (readFrames, loopCount, readDelays) = PngCoder.ReadSequence(path);
            await Assert.That(readFrames.Length).IsEqualTo(3);
            await Assert.That(loopCount).IsEqualTo(0);
            await Assert.That(readDelays.Length).IsEqualTo(3);
            await Assert.That(readDelays[0]).IsEqualTo(100);
            await Assert.That(readDelays[1]).IsEqualTo(200);
            await Assert.That(readDelays[2]).IsEqualTo(150);

            // Verify first frame is red
            var pix0 = readFrames[0].GetPixelRow(0);
            byte r0 = Quantum.ScaleToByte(pix0[0]);
            byte g0 = Quantum.ScaleToByte(pix0[1]);
            byte b0 = Quantum.ScaleToByte(pix0[2]);
            await Assert.That(r0).IsEqualTo((byte)255); // R
            await Assert.That(g0).IsEqualTo((byte)0);   // G
            await Assert.That(b0).IsEqualTo((byte)0);   // B

            // Verify second frame is green
            var pix1 = readFrames[1].GetPixelRow(0);
            byte r1 = Quantum.ScaleToByte(pix1[0]);
            byte g1 = Quantum.ScaleToByte(pix1[1]);
            byte b1 = Quantum.ScaleToByte(pix1[2]);
            await Assert.That(r1).IsEqualTo((byte)0);
            await Assert.That(g1).IsEqualTo((byte)255);
            await Assert.That(b1).IsEqualTo((byte)0);

            // Verify third frame is blue
            var pix2 = readFrames[2].GetPixelRow(0);
            byte r2 = Quantum.ScaleToByte(pix2[0]);
            byte g2 = Quantum.ScaleToByte(pix2[1]);
            byte b2 = Quantum.ScaleToByte(pix2[2]);
            await Assert.That(r2).IsEqualTo((byte)0);
            await Assert.That(g2).IsEqualTo((byte)0);
            await Assert.That(b2).IsEqualTo((byte)255);

            foreach (var f in frames) f.Dispose();
            foreach (var f in readFrames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task WriteSequence_SingleFrame_CreatesValidApng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_single_{Guid.NewGuid()}.png");
        try
        {
            var frames = new[] { CreateSolidFrame(32, 32, Quantum.MaxValue, Quantum.MaxValue, 0) };
            int[] delays = [500];

            PngCoder.WriteSequence(frames, path, delays);
            await Assert.That(File.Exists(path)).IsTrue();

            var (readFrames, _, readDelays) = PngCoder.ReadSequence(path);
            await Assert.That(readFrames.Length).IsEqualTo(1);
            await Assert.That(readDelays[0]).IsEqualTo(500);

            foreach (var f in frames) f.Dispose();
            foreach (var f in readFrames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task ReadSequence_StaticPng_ReturnsSingleFrame()
    {
        // Read a regular (non-animated) PNG through ReadSequence
        string testAssetDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        string wizardPath = Path.Combine(testAssetDir, "peppers_rgba.png");

        var (frames, loopCount, delaysMs) = PngCoder.ReadSequence(wizardPath);
        await Assert.That(frames.Length).IsEqualTo(1);
        await Assert.That(frames[0].Columns).IsEqualTo((uint)1104);
        await Assert.That(frames[0].Rows).IsEqualTo((uint)1468);

        foreach (var f in frames) f.Dispose();
    }

    [Test]
    public async Task WriteSequence_LoopCount_PreservedOnRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_loop_{Guid.NewGuid()}.png");
        try
        {
            var frames = new[]
            {
                CreateSolidFrame(16, 16, Quantum.MaxValue, 0, 0),
                CreateSolidFrame(16, 16, 0, Quantum.MaxValue, 0),
            };

            PngCoder.WriteSequence(frames, path, [100, 100], loopCount: 5);
            var (readFrames, loopCount, _) = PngCoder.ReadSequence(path);
            await Assert.That(loopCount).IsEqualTo(5);

            foreach (var f in frames) f.Dispose();
            foreach (var f in readFrames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task WriteSequence_FileStartsWithPngSignature()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_sig_{Guid.NewGuid()}.png");
        try
        {
            var frames = new[] { CreateSolidFrame(8, 8, 0, 0, Quantum.MaxValue) };
            PngCoder.WriteSequence(frames, path, [100]);

            byte[] header = new byte[8];
            using var fs = File.OpenRead(path);
            fs.ReadExactly(header);
            await Assert.That(PngCoder.CanDecode(header)).IsTrue();

            foreach (var f in frames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task WriteSequence_FrameDimensions_Consistent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_dim_{Guid.NewGuid()}.png");
        try
        {
            var frames = new[]
            {
                CreateSolidFrame(100, 50, Quantum.MaxValue, 0, 0),
                CreateSolidFrame(100, 50, 0, Quantum.MaxValue, 0),
            };

            PngCoder.WriteSequence(frames, path, [100, 100]);
            var (readFrames, _, _) = PngCoder.ReadSequence(path);

            for (int i = 0; i < readFrames.Length; i++)
            {
                await Assert.That(readFrames[i].Columns).IsEqualTo((uint)100);
                await Assert.That(readFrames[i].Rows).IsEqualTo((uint)50);
            }

            foreach (var f in frames) f.Dispose();
            foreach (var f in readFrames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task WriteSequence_EmptyArray_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_empty_{Guid.NewGuid()}.png");
        try
        {
            await Assert.That(() => PngCoder.WriteSequence([], path, [])).Throws<ArgumentException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task RoundTrip_GradientFrame_PixelAccuracy()
    {
        string path = Path.Combine(Path.GetTempPath(), $"apng_gradient_{Guid.NewGuid()}.png");
        try
        {
            var frame = new ImageFrame();
            frame.Initialize(64, 64, ColorspaceType.SRGB, true);
            for (int y = 0; y < 64; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < 64; x++)
                {
                    int offset = x * 4;
                    row[offset] = Quantum.ScaleFromByte((byte)(x * 4));     // R gradient
                    row[offset + 1] = Quantum.ScaleFromByte((byte)(y * 4)); // G gradient
                    row[offset + 2] = Quantum.ScaleFromByte(128);           // B constant
                    row[offset + 3] = Quantum.MaxValue;
                }
            }

            PngCoder.WriteSequence([frame], path, [100]);
            var (readFrames, _, _) = PngCoder.ReadSequence(path);
            await Assert.That(readFrames.Length).IsEqualTo(1);

            // Check a sample pixel
            var readRow = readFrames[0].GetPixelRow(32);
            byte readR = Quantum.ScaleToByte(readRow[32 * 4]);
            byte readG = Quantum.ScaleToByte(readRow[32 * 4 + 1]);
            byte readB = Quantum.ScaleToByte(readRow[32 * 4 + 2]);
            await Assert.That(readR).IsEqualTo((byte)128);
            await Assert.That(readG).IsEqualTo((byte)128);
            await Assert.That(readB).IsEqualTo((byte)128);

            frame.Dispose();
            foreach (var f in readFrames) f.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
