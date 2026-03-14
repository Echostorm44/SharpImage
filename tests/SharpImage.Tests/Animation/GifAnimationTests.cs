using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Animation;

/// <summary>
/// Tests for GIF animation: multi-frame read/write, frame timing, disposal methods,
/// loop count, and round-trip fidelity.
/// </summary>
public class GifAnimationTests
{
    /// <summary>
    /// Creates a simple animated GIF sequence with solid-color frames.
    /// </summary>
    private static ImageSequence CreateTestAnimation(int frameCount = 3, int width = 8, int height = 8)
    {
        var sequence = new ImageSequence
        {
            CanvasWidth = width,
            CanvasHeight = height,
            LoopCount = 0 // infinite loop
        };

        ushort[] colors =
        [
            Quantum.MaxValue, 0, 0,                    // red
            0, Quantum.MaxValue, 0,                    // green
            0, 0, Quantum.MaxValue,                    // blue
            Quantum.MaxValue, Quantum.MaxValue, 0,     // yellow
            Quantum.MaxValue, 0, Quantum.MaxValue,     // magenta
        ];

        for (int i = 0; i < frameCount; i++)
        {
            var frame = new ImageFrame();
            frame.Initialize(width, height, ColorspaceType.SRGB, false);
            frame.Delay = 10 * (i + 1); // 100ms, 200ms, 300ms...

            int colorIdx = (i % 5) * 3;
            ushort r = colors[colorIdx], g = colors[colorIdx + 1], b = colors[colorIdx + 2];

            for (int y = 0; y < height; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < width; x++)
                {
                    row[x * 3] = r;
                    row[x * 3 + 1] = g;
                    row[x * 3 + 2] = b;
                }
            }

            sequence.AddFrame(frame);
        }

        return sequence;
    }

    [Test]
    public async Task ImageSequence_AddFrame_TracksFrameCount()
    {
        using var seq = CreateTestAnimation(4);
        await Assert.That(seq.Count).IsEqualTo(4);
        await Assert.That(seq.CanvasWidth).IsEqualTo(8);
        await Assert.That(seq.CanvasHeight).IsEqualTo(8);
    }

    [Test]
    public async Task ImageSequence_Frames_HaveCorrectSceneNumbers()
    {
        using var seq = CreateTestAnimation(3);
        await Assert.That(seq[0].Scene).IsEqualTo(0);
        await Assert.That(seq[1].Scene).IsEqualTo(1);
        await Assert.That(seq[2].Scene).IsEqualTo(2);
    }

    [Test]
    public async Task ImageSequence_TotalDuration_IsSum()
    {
        using var seq = CreateTestAnimation(3);
        // Delays: 10, 20, 30 = total 60 centiseconds
        await Assert.That(seq.TotalDuration).IsEqualTo(60);
    }

    [Test]
    public async Task ImageSequence_RemoveFrame_UpdatesScenes()
    {
        using var seq = CreateTestAnimation(3);
        seq.RemoveFrame(1); // remove middle frame
        await Assert.That(seq.Count).IsEqualTo(2);
        await Assert.That(seq[0].Scene).IsEqualTo(0);
        await Assert.That(seq[1].Scene).IsEqualTo(1);
    }

    [Test]
    public async Task GifAnimation_WriteAndReadBack_PreservesFrameCount()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_test_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(3);
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(3);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_RoundTrip_PreservesFrameDelays()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_delay_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(3);
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded[0].Delay).IsEqualTo(10);
            await Assert.That(loaded[1].Delay).IsEqualTo(20);
            await Assert.That(loaded[2].Delay).IsEqualTo(30);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_RoundTrip_PreservesLoopCount()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_loop_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(2);
            original.LoopCount = 5;
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.LoopCount).IsEqualTo(5);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_RoundTrip_PreservesFrameColors()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_colors_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(3);
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);

            // Frame 0 should be red
            byte r0 = Quantum.ScaleToByte(loaded[0].GetPixelRow(0)[0]);
            byte g0 = Quantum.ScaleToByte(loaded[0].GetPixelRow(0)[1]);
            byte b0 = Quantum.ScaleToByte(loaded[0].GetPixelRow(0)[2]);
            await Assert.That(r0).IsEqualTo((byte)255);
            await Assert.That(g0).IsEqualTo((byte)0);
            await Assert.That(b0).IsEqualTo((byte)0);

            // Frame 1 should be green
            byte r1 = Quantum.ScaleToByte(loaded[1].GetPixelRow(0)[0]);
            byte g1 = Quantum.ScaleToByte(loaded[1].GetPixelRow(0)[1]);
            byte b1 = Quantum.ScaleToByte(loaded[1].GetPixelRow(0)[2]);
            await Assert.That(r1).IsEqualTo((byte)0);
            await Assert.That(g1).IsEqualTo((byte)255);
            await Assert.That(b1).IsEqualTo((byte)0);

            // Frame 2 should be blue
            byte r2 = Quantum.ScaleToByte(loaded[2].GetPixelRow(0)[0]);
            byte g2 = Quantum.ScaleToByte(loaded[2].GetPixelRow(0)[1]);
            byte b2 = Quantum.ScaleToByte(loaded[2].GetPixelRow(0)[2]);
            await Assert.That(r2).IsEqualTo((byte)0);
            await Assert.That(g2).IsEqualTo((byte)0);
            await Assert.That(b2).IsEqualTo((byte)255);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_RoundTrip_PreservesDisposalMethod()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_dispose_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(2);
            original[0].DisposeMethod = DisposeType.Background;
            original[1].DisposeMethod = DisposeType.None;
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded[0].DisposeMethod).IsEqualTo(DisposeType.Background);
            await Assert.That(loaded[1].DisposeMethod).IsEqualTo(DisposeType.None);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_SingleFrame_WritesValidGif()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_single_{Guid.NewGuid():N}.gif");
        try
        {
            using var seq = CreateTestAnimation(1);
            GifCoder.WriteSequence(seq, tempFile);

            // Should be readable as both sequence and single frame
            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(1);

            var singleFrame = GifCoder.Read(tempFile);
            await Assert.That(singleFrame.Columns).IsEqualTo(8);
            singleFrame.Dispose();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_ManyFrames_WritesAndReads()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_many_{Guid.NewGuid():N}.gif");
        try
        {
            using var seq = CreateTestAnimation(10, 4, 4);
            GifCoder.WriteSequence(seq, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(10);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_Transparency_IsPreserved()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_trans_{Guid.NewGuid():N}.gif");
        try
        {
            var sequence = new ImageSequence
            {
                CanvasWidth = 4,
                CanvasHeight = 4,
                LoopCount = 0
            };

            // Frame with half-transparent pixels
            var frame = new ImageFrame();
            frame.Initialize(4, 4, ColorspaceType.SRGB, true);
            frame.Delay = 10;

            for (int y = 0; y < 4; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < 4; x++)
                {
                    row[x * 4] = Quantum.MaxValue;     // R
                    row[x * 4 + 1] = 0;                // G
                    row[x * 4 + 2] = 0;                // B
                    row[x * 4 + 3] = (x < 2) ? Quantum.MaxValue : (ushort)0; // A: left opaque, right transparent
                }
            }

            sequence.AddFrame(frame);
            GifCoder.WriteSequence(sequence, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(1);
            await Assert.That(loaded[0].HasAlpha).IsTrue();

            // Left pixel should be opaque
            ushort leftAlpha = loaded[0].GetPixelRow(0)[3];
            ushort rightAlpha = loaded[0].GetPixelRow(0)[2 * 4 + 3];
            await Assert.That(leftAlpha).IsEqualTo(Quantum.MaxValue);
            // Right pixel should be transparent
            await Assert.That(rightAlpha).IsEqualTo((ushort)0);

            sequence.Dispose();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task FormatRegistry_ReadSequence_ReadsGif()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_reg_{Guid.NewGuid():N}.gif");
        try
        {
            using var original = CreateTestAnimation(2);
            GifCoder.WriteSequence(original, tempFile);

            using var loaded = FormatRegistry.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task FormatRegistry_WriteSequence_WritesGif()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_wreg_{Guid.NewGuid():N}.gif");
        try
        {
            using var seq = CreateTestAnimation(2);
            FormatRegistry.WriteSequence(seq, tempFile);

            using var loaded = FormatRegistry.ReadSequence(tempFile);
            await Assert.That(loaded.Count).IsEqualTo(2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task GifAnimation_CanvasSize_IsCorrect()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"si_anim_canvas_{Guid.NewGuid():N}.gif");
        try
        {
            using var seq = CreateTestAnimation(2, 16, 12);
            GifCoder.WriteSequence(seq, tempFile);

            using var loaded = GifCoder.ReadSequence(tempFile);
            await Assert.That(loaded.CanvasWidth).IsEqualTo(16);
            await Assert.That(loaded.CanvasHeight).IsEqualTo(12);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
