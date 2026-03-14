using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class TextureOpsTests
{
    private static ImageFrame CreateCheckerboard(uint w, uint h, int blockSize = 8)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                bool white = ((x / blockSize) + (y / blockSize)) % 2 == 0;
                ushort val = white ? Quantum.MaxValue : (ushort)0;
                int off = x * 3;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradient(uint w, uint h)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, false);
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / (int)w);
                int off = x * 3;
                row[off] = val; row[off + 1] = val; row[off + 2] = val;
            }
        }
        return frame;
    }

    // ─── Seamless Tile Tests ────────────────────────────────────

    [Test]
    public async Task SeamlessTile_PreservesDimensions()
    {
        using var src = CreateGradient(50, 40);
        using var result = TextureOps.MakeSeamlessTile(src, blendWidth: 0.25);

        await Assert.That(result.Columns).IsEqualTo(50u);
        await Assert.That(result.Rows).IsEqualTo(40u);
    }

    [Test]
    public async Task SeamlessTile_EdgesMatch()
    {
        using var src = CreateGradient(40, 40);
        using var result = TextureOps.MakeSeamlessTile(src, blendWidth: 0.3);

        // Left edge and right edge should be close (seamless wrap)
        var topRow = result.GetPixelRow(20).ToArray();
        int leftVal = topRow[0];
        int rightVal = topRow[39 * 3];
        int diff = Math.Abs(leftVal - rightVal);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 3);
    }

    [Test]
    public async Task SeamlessTile_TopBottomMatch()
    {
        using var src = CreateCheckerboard(40, 40, 4);
        using var result = TextureOps.MakeSeamlessTile(src, blendWidth: 0.25);

        var topRow = result.GetPixelRow(0).ToArray();
        var botRow = result.GetPixelRow(39).ToArray();
        int diff = Math.Abs(topRow[60] - botRow[60]);
        await Assert.That(diff).IsLessThan(Quantum.MaxValue / 3);
    }

    // ─── Texture Synthesis Tests ────────────────────────────────

    [Test]
    public async Task SynthesizeTexture_ProducesCorrectSize()
    {
        using var exemplar = CreateCheckerboard(16, 16, 4);
        using var result = TextureOps.SynthesizeTexture(exemplar, 40, 30, neighborhoodRadius: 2, seed: 123);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
    }

    [Test]
    public async Task SynthesizeTexture_ContainsExemplarColors()
    {
        using var exemplar = CreateCheckerboard(12, 12, 4);
        using var result = TextureOps.SynthesizeTexture(exemplar, 30, 30, neighborhoodRadius: 2, seed: 42);

        // Result should contain both black and white (the exemplar colors)
        bool hasBlack = false, hasWhite = false;
        for (int y = 0; y < 30 && !(hasBlack && hasWhite); y++)
        {
            var row = result.GetPixelRow(y).ToArray();
            for (int x = 0; x < 30; x++)
            {
                int v = row[x * 3];
                if (v < Quantum.MaxValue / 4) hasBlack = true;
                if (v > Quantum.MaxValue * 3 / 4) hasWhite = true;
            }
        }
        await Assert.That(hasBlack).IsTrue();
        await Assert.That(hasWhite).IsTrue();
    }

    [Test]
    public async Task SynthesizeTexture_Reproducible()
    {
        using var exemplar = CreateCheckerboard(10, 10, 5);
        using var r1 = TextureOps.SynthesizeTexture(exemplar, 20, 20, neighborhoodRadius: 2, seed: 99);
        using var r2 = TextureOps.SynthesizeTexture(exemplar, 20, 20, neighborhoodRadius: 2, seed: 99);

        var row1 = r1.GetPixelRow(10).ToArray();
        var row2 = r2.GetPixelRow(10).ToArray();
        await Assert.That(row1[30]).IsEqualTo(row2[30]);
    }
}
