using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

public class AdvancedTransformTests
{
    private static ImageFrame CreateGradient(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = (ushort)(x * Quantum.MaxValue / (width - 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / (height - 1));
                row[off + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    private static ImageFrame CreateSharpPattern(int width, int height, bool horizontal)
    {
        // Alternating stripes — high local contrast (sharp)
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                bool stripe = horizontal ? (y % 4 < 2) : (x % 4 < 2);
                ushort val = stripe ? Quantum.MaxValue : (ushort)0;
                row[off] = row[off + 1] = row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateBlurryPattern(int width, int height)
    {
        // Uniform midgray — no local contrast (blurry)
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = row[off + 1] = row[off + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    // ─── Liquify Tests ─────────────────────────────────────────────

    [Test]
    public async Task Liquify_ProducesCorrectSize()
    {
        var image = CreateGradient(40, 30);
        var result = AdvancedTransform.Liquify(image, 20, 15, 10, 0.8, 5, 0);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task Liquify_ZeroStrengthNoChange()
    {
        var image = CreateGradient(30, 20);
        var result = AdvancedTransform.Liquify(image, 15, 10, 8, 0.0, 10, 0);

        // Zero strength should produce identical image
        var origArr = image.GetPixelRow(10).ToArray();
        var resArr = result.GetPixelRow(10).ToArray();
        bool identical = true;
        for (int i = 0; i < origArr.Length; i++)
            if (origArr[i] != resArr[i]) { identical = false; break; }
        await Assert.That(identical).IsTrue();
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task Liquify_PixelsOutsideRadiusUnchanged()
    {
        var image = CreateGradient(60, 40);
        // Liquify at center with radius 5
        var result = AdvancedTransform.Liquify(image, 30, 20, 5, 1.0, 10, 0);

        // Corner pixel (far from center) should be unchanged
        var origArr = image.GetPixelRow(0).ToArray();
        var resArr = result.GetPixelRow(0).ToArray();
        await Assert.That(resArr[0]).IsEqualTo(origArr[0]);
        await Assert.That(resArr[1]).IsEqualTo(origArr[1]);
        await Assert.That(resArr[2]).IsEqualTo(origArr[2]);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task LiquifyMulti_AppliesMultiplePushes()
    {
        var image = CreateGradient(40, 30);
        var pushes = new (int, int, double, double)[]
        {
            (10, 15, 3, 0),
            (30, 15, -3, 0),
        };
        var result = AdvancedTransform.LiquifyMulti(image, pushes, 8, 0.5);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
        image.Dispose(); result.Dispose();
    }

    // ─── Frequency Separation Tests ────────────────────────────────

    [Test]
    public async Task FrequencySeparation_ProducesCorrectSizes()
    {
        var image = CreateGradient(40, 30);
        var (low, high) = AdvancedTransform.FrequencySeparation(image, blurRadius: 3.0);

        await Assert.That(low.Columns).IsEqualTo(40u);
        await Assert.That(low.Rows).IsEqualTo(30u);
        await Assert.That(high.Columns).IsEqualTo(40u);
        await Assert.That(high.Rows).IsEqualTo(30u);
        image.Dispose(); low.Dispose(); high.Dispose();
    }

    [Test]
    public async Task FrequencyRecombine_RestoresOriginal()
    {
        var image = CreateGradient(30, 20);
        var (low, high) = AdvancedTransform.FrequencySeparation(image, blurRadius: 3.0);
        var recombined = AdvancedTransform.FrequencyRecombine(low, high);

        // Recombined should be very close to original
        int maxDiff = 0;
        for (int y = 2; y < 18; y++) // avoid edges (blur boundary)
        {
            var origArr = image.GetPixelRow(y).ToArray();
            var resArr = recombined.GetPixelRow(y).ToArray();
            for (int i = 6; i < origArr.Length - 6; i++)
            {
                int diff = Math.Abs(origArr[i] - resArr[i]);
                if (diff > maxDiff) maxDiff = diff;
            }
        }
        // Should reconstruct within rounding error
        await Assert.That(maxDiff).IsLessThan(200); // small rounding errors acceptable
        image.Dispose(); low.Dispose(); high.Dispose(); recombined.Dispose();
    }

    [Test]
    public async Task FrequencySeparation_HighFreqIsMidgrayForUniform()
    {
        // Uniform image should have no high-frequency content
        var image = CreateBlurryPattern(30, 20);
        var (low, high) = AdvancedTransform.FrequencySeparation(image, blurRadius: 3.0);

        // High freq should be near midgray (32767-32768)
        int midgray = Quantum.MaxValue / 2;
        var highArr = high.GetPixelRow(10).ToArray();
        int diff = Math.Abs(highArr[45] - midgray); // center pixel, R channel
        await Assert.That(diff).IsLessThan(500);
        image.Dispose(); low.Dispose(); high.Dispose();
    }

    // ─── Focus Stacking Tests ──────────────────────────────────────

    [Test]
    public async Task FocusStack_SingleImageReturnsClone()
    {
        var image = CreateGradient(30, 20);
        var result = AdvancedTransform.FocusStack([image]);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task FocusStack_PicksSharpestRegions()
    {
        // Image 1: sharp top, blurry bottom
        var img1 = new ImageFrame();
        img1.Initialize(30, 20, ColorspaceType.RGB, false);
        for (int y = 0; y < 20; y++)
        {
            var row = img1.GetPixelRowForWrite(y);
            for (int x = 0; x < 30; x++)
            {
                int off = x * 3;
                if (y < 10)
                {
                    // Sharp stripe pattern
                    bool stripe = x % 4 < 2;
                    ushort v = stripe ? Quantum.MaxValue : (ushort)0;
                    row[off] = row[off + 1] = row[off + 2] = v;
                }
                else
                {
                    row[off] = row[off + 1] = row[off + 2] = (ushort)(Quantum.MaxValue / 2);
                }
            }
        }

        // Image 2: blurry top, sharp bottom
        var img2 = new ImageFrame();
        img2.Initialize(30, 20, ColorspaceType.RGB, false);
        for (int y = 0; y < 20; y++)
        {
            var row = img2.GetPixelRowForWrite(y);
            for (int x = 0; x < 30; x++)
            {
                int off = x * 3;
                if (y >= 10)
                {
                    bool stripe = x % 4 < 2;
                    ushort v = stripe ? Quantum.MaxValue : (ushort)0;
                    row[off] = row[off + 1] = row[off + 2] = v;
                }
                else
                {
                    row[off] = row[off + 1] = row[off + 2] = (ushort)(Quantum.MaxValue / 2);
                }
            }
        }

        var result = AdvancedTransform.FocusStack([img1, img2]);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);

        // Top should have stripe pattern from img1
        var topArr = result.GetPixelRow(5).ToArray();
        bool hasContrast = Math.Abs(topArr[0] - topArr[6]) > Quantum.MaxValue / 2;
        await Assert.That(hasContrast).IsTrue();

        // Bottom should have stripe pattern from img2
        var botArr = result.GetPixelRow(15).ToArray();
        bool botContrast = Math.Abs(botArr[0] - botArr[6]) > Quantum.MaxValue / 2;
        await Assert.That(botContrast).IsTrue();

        img1.Dispose(); img2.Dispose(); result.Dispose();
    }
}
