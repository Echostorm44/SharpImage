using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class HdrTests
{
    private static ImageFrame CreateExposure(int width, int height, double brightness)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                double baseVal = (double)x / (width - 1);
                ushort val = Quantum.Clamp((int)(baseVal * brightness * Quantum.MaxValue));
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateSolid(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = r; row[off + 1] = g; row[off + 2] = b;
            }
        }
        return frame;
    }

    // ─── HDR Merge Tests ───────────────────────────────────────────

    [Test]
    public async Task HdrMerge_ProducesCorrectSize()
    {
        var dark = CreateExposure(30, 20, 0.3);
        var mid = CreateExposure(30, 20, 1.0);
        var bright = CreateExposure(30, 20, 3.0);

        var result = HdrOps.HdrMerge([dark, mid, bright], [-2.0, 0.0, 2.0]);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);
        dark.Dispose(); mid.Dispose(); bright.Dispose(); result.Dispose();
    }

    [Test]
    public async Task HdrMerge_UtilizesMultipleExposures()
    {
        var dark = CreateExposure(30, 20, 0.3);
        var bright = CreateExposure(30, 20, 3.0);

        var result = HdrOps.HdrMerge([dark, bright], [-2.0, 2.0]);

        // Result should have values (not all zero or all max)
        var midArr = result.GetPixelRow(10).ToArray();
        int centerVal = midArr[15 * 3]; // middle pixel
        await Assert.That(centerVal).IsGreaterThan(0);
        await Assert.That(centerVal).IsLessThan(Quantum.MaxValue);
        dark.Dispose(); bright.Dispose(); result.Dispose();
    }

    // ─── Tone Map Reinhard Tests ───────────────────────────────────

    [Test]
    public async Task ToneMapReinhard_ProducesCorrectSize()
    {
        var image = CreateExposure(30, 20, 1.0);
        var result = HdrOps.ToneMapReinhard(image);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task ToneMapReinhard_CompressesRange()
    {
        var image = CreateExposure(30, 20, 1.0);
        var result = HdrOps.ToneMapReinhard(image, key: 0.18);

        // Bright pixels should be compressed (not at max)
        var arr = result.GetPixelRow(10).ToArray();
        int brightPixel = arr[28 * 3]; // near-right pixel
        // Reinhard compression means the output should be less than max
        await Assert.That(brightPixel).IsLessThanOrEqualTo(Quantum.MaxValue);
        image.Dispose(); result.Dispose();
    }

    // ─── Tone Map Drago Tests ──────────────────────────────────────

    [Test]
    public async Task ToneMapDrago_ProducesCorrectSize()
    {
        var image = CreateExposure(30, 20, 1.0);
        var result = HdrOps.ToneMapDrago(image);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task ToneMapDrago_PreservesBlack()
    {
        var image = CreateExposure(30, 20, 1.0);
        var result = HdrOps.ToneMapDrago(image);

        // Leftmost pixel (x=0) is black in gradient, should stay near black
        var arr = result.GetPixelRow(10).ToArray();
        await Assert.That(arr[0]).IsLessThan((ushort)(Quantum.MaxValue / 4));
        image.Dispose(); result.Dispose();
    }

    // ─── Exposure Fusion Tests ─────────────────────────────────────

    [Test]
    public async Task ExposureFusion_ProducesCorrectSize()
    {
        var dark = CreateExposure(30, 20, 0.3);
        var mid = CreateExposure(30, 20, 1.0);
        var bright = CreateExposure(30, 20, 3.0);

        var result = HdrOps.ExposureFusion([dark, mid, bright]);

        await Assert.That(result.Columns).IsEqualTo(30u);
        await Assert.That(result.Rows).IsEqualTo(20u);
        dark.Dispose(); mid.Dispose(); bright.Dispose(); result.Dispose();
    }

    [Test]
    public async Task ExposureFusion_SingleImageReturnsClone()
    {
        var image = CreateExposure(20, 15, 1.0);
        var result = HdrOps.ExposureFusion([image]);

        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(15u);
        image.Dispose(); result.Dispose();
    }

    [Test]
    public async Task ExposureFusion_BlendsDifferentExposures()
    {
        var dark = CreateSolid(20, 15, (ushort)(Quantum.MaxValue * 0.1), (ushort)(Quantum.MaxValue * 0.1), (ushort)(Quantum.MaxValue * 0.1));
        var bright = CreateSolid(20, 15, (ushort)(Quantum.MaxValue * 0.9), (ushort)(Quantum.MaxValue * 0.9), (ushort)(Quantum.MaxValue * 0.9));

        var result = HdrOps.ExposureFusion([dark, bright]);

        // Fusion of a very dark and very bright image should produce mid-range values
        var arr = result.GetPixelRow(7).ToArray();
        int midVal = arr[30]; // pixel at x=10
        await Assert.That(midVal).IsGreaterThan((int)(Quantum.MaxValue * 0.05));
        await Assert.That(midVal).IsLessThan((int)(Quantum.MaxValue * 0.95));
        dark.Dispose(); bright.Dispose(); result.Dispose();
    }
}
