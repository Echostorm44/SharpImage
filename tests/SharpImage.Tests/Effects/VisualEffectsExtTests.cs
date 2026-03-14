using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for Phase 46 visual effects: Morph, Anaglyph, Enhance, ChromaKey, ChannelFx, CDL.
/// </summary>
public class VisualEffectsExtTests
{
    // ─── Morph ───────────────────────────────────────────────────

    [Test]
    public async Task Morph_ProducesCorrectFrameCount()
    {
        using var a = CreateSolid(16, 16, 0);
        using var b = CreateSolid(16, 16, Quantum.MaxValue);

        var frames = MorphOps.Morph(a, b, 3);

        await Assert.That(frames.Length).IsEqualTo(5); // 3 intermediate + 2 endpoints
        foreach (var f in frames) f.Dispose();
    }

    [Test]
    public async Task Morph_MiddleFrame_IsAverage()
    {
        using var black = CreateSolid(16, 16, 0);
        using var white = CreateSolid(16, 16, Quantum.MaxValue);

        var frames = MorphOps.Morph(black, white, 1);
        // Middle frame (index 1) should be ~50% gray
        var row = frames[1].GetPixelRow(0);
        int mid = Quantum.MaxValue / 2;
        await Assert.That(Math.Abs(row[0] - mid)).IsLessThanOrEqualTo(1);
        foreach (var f in frames) f.Dispose();
    }

    // ─── Anaglyph ────────────────────────────────────────────────

    [Test]
    public async Task Anaglyph_PreservesDimensions()
    {
        using var left = CreateSolid(24, 16, 30000);
        using var right = CreateSolid(24, 16, 10000);

        using var result = StereoOps.Anaglyph(left, right);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    [Test]
    public async Task Anaglyph_RedFromLeft_CyanFromRight()
    {
        using var left = CreateColor(16, 16, Quantum.MaxValue, 0, 0); // pure red
        using var right = CreateColor(16, 16, 0, Quantum.MaxValue, Quantum.MaxValue); // cyan

        using var result = StereoOps.Anaglyph(left, right);

        var row = result.GetPixelRow(0).ToArray();
        // Red channel should come from left (luminance of red = 0.299 * Max ≈ 19595)
        await Assert.That(row[0]).IsGreaterThan((ushort)0);
        // Green and blue from right
        await Assert.That((int)row[1]).IsEqualTo(Quantum.MaxValue);
        await Assert.That((int)row[2]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── NoiseReduce (Enhance) ───────────────────────────────────

    [Test]
    public async Task Enhance_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var result = NoiseReduceOps.Enhance(source);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    [Test]
    public async Task Enhance_ReducesNoise()
    {
        // Create a noisy checkerboard-like pattern
        using var source = CreateCheckerboard(16, 16);
        using var result = NoiseReduceOps.Enhance(source);

        // Enhanced result should be smoother — variance should decrease
        var srcRow = source.GetPixelRow(8).ToArray();
        var dstRow = result.GetPixelRow(8).ToArray();

        double srcVar = ComputeVariance(srcRow);
        double dstVar = ComputeVariance(dstRow);

        await Assert.That(dstVar).IsLessThan(srcVar);
    }

    // ─── ChromaKey ───────────────────────────────────────────────

    [Test]
    public async Task ChromaKey_RemovesGreen()
    {
        // Create a pure green image
        using var source = CreateColor(16, 16, 0, Quantum.MaxValue, 0);
        ushort keyG = Quantum.MaxValue;

        using var result = ChromaKeyOps.ChromaKey(source, 0, keyG, 0, 0.15);

        await Assert.That(result.HasAlpha).IsTrue();
        // Pure green pixels should have near-zero alpha
        var row = result.GetPixelRow(0);
        int channels = result.NumberOfChannels;
        await Assert.That((int)row[channels - 1]).IsLessThan(Quantum.MaxValue / 10);
    }

    [Test]
    public async Task ChromaKey_PreservesNonKeyPixels()
    {
        // Red pixels should survive green-keying
        using var source = CreateColor(16, 16, Quantum.MaxValue, 0, 0);

        using var result = ChromaKeyOps.ChromaKey(source, 0, Quantum.MaxValue, 0, 0.15);

        var row = result.GetPixelRow(0);
        int channels = result.NumberOfChannels;
        // Alpha should be full (opaque) for non-green pixels
        await Assert.That((int)row[channels - 1]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── ChannelFx ───────────────────────────────────────────────

    [Test]
    public async Task ChannelFx_CopyRedToBlue()
    {
        using var source = CreateColor(16, 16, Quantum.MaxValue, 0, 0); // pure red

        using var result = ChannelFxOps.ChannelFx(source, "red=>blue");

        var row = result.GetPixelRow(0);
        int ch = result.NumberOfChannels;
        // Blue should now equal red
        await Assert.That((int)row[2]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task ChannelFx_SwapRedGreen()
    {
        using var source = CreateColor(16, 16, Quantum.MaxValue, 0, 1000);

        using var result = ChannelFxOps.ChannelFx(source, "swap:red,green");

        var row = result.GetPixelRow(0).ToArray();
        // After swap: green should have max, red should have 0
        await Assert.That((int)row[0]).IsEqualTo(0);
        await Assert.That((int)row[1]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── CDL ─────────────────────────────────────────────────────

    [Test]
    public async Task Cdl_Identity_PreservesPixels()
    {
        using var source = CreateGradient(16, 16);

        // Identity CDL: slope=1, offset=0, power=1, sat=1
        using var result = CdlOps.ApplyCdl(source, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1.0);

        var srcRow = source.GetPixelRow(8).ToArray();
        var dstRow = result.GetPixelRow(8).ToArray();

        bool same = true;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (Math.Abs(srcRow[i] - dstRow[i]) > 1) { same = false; break; }
        }

        await Assert.That(same).IsTrue();
    }

    [Test]
    public async Task Cdl_HighSlope_BrightensImage()
    {
        using var source = CreateSolid(16, 16, Quantum.MaxValue / 4);

        using var result = CdlOps.ApplyCdl(source, 2.0, 2.0, 2.0, 0, 0, 0, 1, 1, 1);

        var row = result.GetPixelRow(0);
        // With slope=2, value should double (clamped to max)
        await Assert.That((int)row[0]).IsGreaterThan(Quantum.MaxValue / 4);
    }

    [Test]
    public async Task Cdl_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var result = CdlOps.ApplyCdl(source, 1.1, 1.0, 0.9, 0, 0, 0, 1, 1, 1);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateSolid(int width, int height, ushort gray)
    {
        return CreateColor(width, height, gray, gray, gray);
    }

    private static ImageFrame CreateColor(int width, int height, ushort r, ushort g, ushort b)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * channels;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }
        return image;
    }

    private static ImageFrame CreateGradient(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(Quantum.MaxValue * x / Math.Max(width - 1, 1));
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = ((x + y) % 2 == 0) ? Quantum.MaxValue : (ushort)0;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }

    private static double ComputeVariance(ushort[] data)
    {
        double mean = 0;
        for (int i = 0; i < data.Length; i++) mean += data[i];
        mean /= data.Length;

        double variance = 0;
        for (int i = 0; i < data.Length; i++)
        {
            double diff = data[i] - mean;
            variance += diff * diff;
        }
        return variance / data.Length;
    }
}
