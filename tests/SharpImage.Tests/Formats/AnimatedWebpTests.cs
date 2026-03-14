using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

public class AnimatedWebpTests
{
    private static string TestAssetsDir => Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static ImageSequence CreateTestSequence(int frameCount, int width, int height, int delay = 10)
    {
        var sequence = new ImageSequence
        {
            CanvasWidth = width,
            CanvasHeight = height,
            LoopCount = 0,
            FormatName = "WEBP"
        };

        for (int i = 0; i < frameCount; i++)
        {
            var frame = new ImageFrame();
            frame.Initialize(width, height, ColorspaceType.SRGB, false);
            // Fill each frame with a distinct color
            byte shade = (byte)(50 + i * (200 / Math.Max(1, frameCount - 1)));
            ushort q = Quantum.ScaleFromByte(shade);
            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    int off = x * 3;
                    row[off] = (ushort)(q * (i % 3 == 0 ? 1 : 0));
                    row[off + 1] = (ushort)(q * (i % 3 == 1 ? 1 : 0));
                    row[off + 2] = (ushort)(q * (i % 3 == 2 ? 1 : 0));
                }
            }
            frame.Delay = delay;
            sequence.AddFrame(frame);
        }

        return sequence;
    }

    [Test]
    public async Task WriteSequence_ThreeFrames_CreatesValidFile()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_test_3f.webp");
        try
        {
            using var seq = CreateTestSequence(3, 32, 32, 10);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(seq, stream);

            await Assert.That(File.Exists(output)).IsTrue();
            byte[] data = File.ReadAllBytes(output);
            // Verify RIFF header
            await Assert.That(data.Length).IsGreaterThan(20);
            await Assert.That((char)data[0]).IsEqualTo('R');
            await Assert.That((char)data[8]).IsEqualTo('W');
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task WriteSequence_ContainsVP8XAndANMFChunks()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_test_chunks.webp");
        try
        {
            using var seq = CreateTestSequence(3, 16, 16, 5);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(seq, stream);

            byte[] data = File.ReadAllBytes(output);
            string dataStr = System.Text.Encoding.ASCII.GetString(data);
            // Should contain VP8X, ANIM, and ANMF chunks
            await Assert.That(dataStr.Contains("VP8X")).IsTrue();
            await Assert.That(dataStr.Contains("ANIM")).IsTrue();
            await Assert.That(dataStr.Contains("ANMF")).IsTrue();
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task RoundTrip_WriteAndRead_PreservesFrameCount()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_roundtrip.webp");
        try
        {
            using var original = CreateTestSequence(4, 24, 24, 8);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(original, stream);

            using var loaded = FormatRegistry.ReadSequence(output);
            await Assert.That(loaded.Count).IsEqualTo(4);
            await Assert.That(loaded.CanvasWidth).IsEqualTo(24);
            await Assert.That(loaded.CanvasHeight).IsEqualTo(24);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task RoundTrip_PreservesFrameDelay()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_delay.webp");
        try
        {
            using var original = CreateTestSequence(2, 16, 16, 15);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(original, stream);

            ImageSequence loaded;
            using (var readStream = File.OpenRead(output))
                loaded = WebpCoder.ReadSequence(readStream);
            using (loaded)
            {
                // Delay stored as centiseconds, converted to ms in file then back
                await Assert.That(loaded[0].Delay).IsEqualTo(15);
                await Assert.That(loaded[1].Delay).IsEqualTo(15);
            }
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task RoundTrip_PreservesPixelColors()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_pixels.webp");
        try
        {
            using var original = CreateTestSequence(3, 8, 8, 10);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(original, stream);

            ImageSequence loaded;
            using (var readStream = File.OpenRead(output))
                loaded = WebpCoder.ReadSequence(readStream);
            using (loaded)
            {
                await Assert.That(loaded.Count).IsEqualTo(3);

                // Frame 0 should be reddish (i%3==0 → red channel)
                var row0 = loaded[0].GetPixelRow(0);
                ushort r0 = row0[0]; // red channel
                await Assert.That(r0).IsGreaterThan((ushort)0);

                // Frame 1 should be greenish (i%3==1 → green channel)
                var row1 = loaded[1].GetPixelRow(0);
                int ch = loaded[1].NumberOfChannels;
                ushort g1 = row1[1]; // green channel
                await Assert.That(g1).IsGreaterThan((ushort)0);
            }
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task SingleFrame_WriteSequence_ProducesSingleImageWebP()
    {
        string output = Path.Combine(Path.GetTempPath(), "anim_single.webp");
        try
        {
            using var seq = CreateTestSequence(1, 16, 16, 10);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(seq, stream);

            // Single frame sequences should produce a normal (non-animated) WebP
            byte[] data = File.ReadAllBytes(output);
            string dataStr = System.Text.Encoding.ASCII.GetString(data);
            // Should NOT contain ANMF since it's a single frame
            await Assert.That(dataStr.Contains("ANMF")).IsFalse();

            // Should be readable as a regular image
            using var ms = new MemoryStream(data);
            var frame = WebpCoder.Read(ms);
            await Assert.That((int)frame.Columns).IsEqualTo(16);
            frame.Dispose();
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Test]
    public async Task ReadSingleFrameWebP_AsSequence_ReturnsOneFrame()
    {
        // Read a regular single-frame WebP as a sequence
        string wizard = Path.Combine(TestAssetsDir, "peppers_rgba.png");
        string tempWebP = Path.Combine(Path.GetTempPath(), "single_for_seq.webp");
        try
        {
            var img = FormatRegistry.Read(wizard);
            FormatRegistry.Write(img, tempWebP);
            img.Dispose();

            ImageSequence seq;
            using (var readStream = File.OpenRead(tempWebP))
                seq = WebpCoder.ReadSequence(readStream);
            using (seq)
            {
                await Assert.That(seq.Count).IsEqualTo(1);
                await Assert.That((int)seq[0].Columns).IsGreaterThan(0);
            }
        }
        finally
        {
            if (File.Exists(tempWebP)) File.Delete(tempWebP);
        }
    }

    [Test]
    public async Task GifToAnimWebp_Conversion()
    {
        // Create a simple GIF, convert to animated WebP, verify
        string gifPath = Path.Combine(Path.GetTempPath(), "test_anim.gif");
        string webpPath = Path.Combine(Path.GetTempPath(), "test_anim.webp");
        try
        {
            // Create a small GIF animation
            using var gifSeq = CreateTestSequence(3, 16, 16, 10);
            using (var stream = new FileStream(gifPath, FileMode.Create, FileAccess.Write))
                GifCoder.WriteSequence(gifSeq, stream);

            // Read the GIF and write as animated WebP
            using var loadedGif = FormatRegistry.ReadSequence(gifPath);
            using (var stream = new FileStream(webpPath, FileMode.Create, FileAccess.Write))
                WebpCoder.WriteSequence(loadedGif, stream);

            await Assert.That(File.Exists(webpPath)).IsTrue();

            // Verify we can read it back
            using var loadedWebp = FormatRegistry.ReadSequence(webpPath);
            await Assert.That(loadedWebp.Count).IsEqualTo(3);
        }
        finally
        {
            if (File.Exists(gifPath)) File.Delete(gifPath);
            if (File.Exists(webpPath)) File.Delete(webpPath);
        }
    }
}
