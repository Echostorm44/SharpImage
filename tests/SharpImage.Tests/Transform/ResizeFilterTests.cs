using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for all resize interpolation filter types. Verifies dimensions, content preservation,
/// and that each kernel produces valid non-corrupt output for both downscale and upscale.
/// </summary>
public class ResizeFilterTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    // ─── Dimension and content tests for each new filter ────────

    [Test]
    public async Task Resize_Triangle_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Triangle);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Hermite_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Hermite);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Mitchell_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Mitchell);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Catrom_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Catrom);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Gaussian_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Gaussian);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Spline_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Spline);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Sinc_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Sinc);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Lanczos2_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Lanczos2);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Lanczos4_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Lanczos4);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Lanczos5_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Lanczos5);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Hann_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Hann);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Hamming_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Hamming);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Blackman_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Blackman);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Kaiser_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Kaiser);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Parzen_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Parzen);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Bohman_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Bohman);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Bartlett_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Bartlett);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Welch_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Welch);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Lagrange_Downscale()
    {
        using var source = CreateGradientImage(64, 48);
        using var resized = Resize.Apply(source, 32, 24, InterpolationMethod.Lagrange);

        await Assert.That(resized.Columns).IsEqualTo(32);
        await Assert.That(resized.Rows).IsEqualTo(24);
        await AssertHasContent(resized);
    }

    // ─── Upscale tests for representative filters ────────────────

    [Test]
    public async Task Resize_Mitchell_Upscale()
    {
        using var source = CreateGradientImage(16, 12);
        using var resized = Resize.Apply(source, 64, 48, InterpolationMethod.Mitchell);

        await Assert.That(resized.Columns).IsEqualTo(64);
        await Assert.That(resized.Rows).IsEqualTo(48);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Lanczos2_Upscale()
    {
        using var source = CreateGradientImage(16, 12);
        using var resized = Resize.Apply(source, 64, 48, InterpolationMethod.Lanczos2);

        await Assert.That(resized.Columns).IsEqualTo(64);
        await Assert.That(resized.Rows).IsEqualTo(48);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Blackman_Upscale()
    {
        using var source = CreateGradientImage(16, 12);
        using var resized = Resize.Apply(source, 64, 48, InterpolationMethod.Blackman);

        await Assert.That(resized.Columns).IsEqualTo(64);
        await Assert.That(resized.Rows).IsEqualTo(48);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_Kaiser_Upscale()
    {
        using var source = CreateGradientImage(16, 12);
        using var resized = Resize.Apply(source, 64, 48, InterpolationMethod.Kaiser);

        await Assert.That(resized.Columns).IsEqualTo(64);
        await Assert.That(resized.Rows).IsEqualTo(48);
        await AssertHasContent(resized);
    }

    // ─── Real image tests ───────────────────────────────────────

    [Test]
    public async Task Resize_RealImage_Mitchell_HalfSize()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        using var rose = PngCoder.Read(rosePng);
        int halfW = (int)rose.Columns / 2;
        int halfH = (int)rose.Rows / 2;

        using var resized = Resize.Apply(rose, halfW, halfH, InterpolationMethod.Mitchell);

        await Assert.That(resized.Columns).IsEqualTo(halfW);
        await Assert.That(resized.Rows).IsEqualTo(halfH);
        await AssertHasContent(resized);
    }

    [Test]
    public async Task Resize_RealImage_Lanczos4_HalfSize()
    {
        string rosePng = Path.Combine(TestImagesDir, "photo_small.png");
        using var rose = PngCoder.Read(rosePng);
        int halfW = (int)rose.Columns / 2;
        int halfH = (int)rose.Rows / 2;

        using var resized = Resize.Apply(rose, halfW, halfH, InterpolationMethod.Lanczos4);

        await Assert.That(resized.Columns).IsEqualTo(halfW);
        await Assert.That(resized.Rows).IsEqualTo(halfH);
        await AssertHasContent(resized);
    }

    // ─── Alias consistency tests ────────────────────────────────

    [Test]
    public async Task Resize_Triangle_MatchesBilinear()
    {
        using var source = CreateGradientImage(64, 48);
        using var bilinear = Resize.Apply(source, 32, 24, InterpolationMethod.Bilinear);
        using var triangle = Resize.Apply(source, 32, 24, InterpolationMethod.Triangle);

        // Triangle and Bilinear should produce identical output
        for (int y = 0; y < 24; y++)
        {
            var rowA = bilinear.GetPixelRow(y).ToArray();
            var rowB = triangle.GetPixelRow(y).ToArray();
            for (int i = 0; i < rowA.Length; i++)
                await Assert.That(rowB[i]).IsEqualTo(rowA[i]);
        }
    }

    [Test]
    public async Task Resize_Bartlett_MatchesTriangle()
    {
        using var source = CreateGradientImage(64, 48);
        using var triangle = Resize.Apply(source, 32, 24, InterpolationMethod.Triangle);
        using var bartlett = Resize.Apply(source, 32, 24, InterpolationMethod.Bartlett);

        // Bartlett and Triangle should produce identical output
        for (int y = 0; y < 24; y++)
        {
            var rowA = triangle.GetPixelRow(y).ToArray();
            var rowB = bartlett.GetPixelRow(y).ToArray();
            for (int i = 0; i < rowA.Length; i++)
                await Assert.That(rowB[i]).IsEqualTo(rowA[i]);
        }
    }

    [Test]
    public async Task Resize_Catrom_MatchesBicubic()
    {
        using var source = CreateGradientImage(64, 48);
        using var bicubic = Resize.Apply(source, 32, 24, InterpolationMethod.Bicubic);
        using var catrom = Resize.Apply(source, 32, 24, InterpolationMethod.Catrom);

        // Catrom and Bicubic should produce identical output
        for (int y = 0; y < 24; y++)
        {
            var rowA = bicubic.GetPixelRow(y).ToArray();
            var rowB = catrom.GetPixelRow(y).ToArray();
            for (int i = 0; i < rowA.Length; i++)
                await Assert.That(rowB[i]).IsEqualTo(rowA[i]);
        }
    }

    // ─── Quality ordering test ──────────────────────────────────

    [Test]
    public async Task Resize_SmoothFilters_ProduceDifferentResults()
    {
        using var source = CreateGradientImage(128, 96);
        using var mitchell = Resize.Apply(source, 32, 24, InterpolationMethod.Mitchell);
        using var spline = Resize.Apply(source, 32, 24, InterpolationMethod.Spline);
        using var gaussian = Resize.Apply(source, 32, 24, InterpolationMethod.Gaussian);

        // All should have valid content but differ from each other
        await AssertHasContent(mitchell);
        await AssertHasContent(spline);
        await AssertHasContent(gaussian);

        // Mitchell (B=1/3,C=1/3) should differ from Spline (B=1,C=0) — different sharpness
        var mitchRow = mitchell.GetPixelRow(12).ToArray();
        var splineRow = spline.GetPixelRow(12).ToArray();
        bool differ = false;
        for (int i = 0; i < mitchRow.Length; i++)
            if (mitchRow[i] != splineRow[i]) { differ = true; break; }
        await Assert.That(differ).IsTrue();
    }

    // ─── Helper Methods ─────────────────────────────────────────

    private static async Task AssertHasContent(ImageFrame image)
    {
        int midY = (int)image.Rows / 2;
        var midRow = image.GetPixelRow(midY).ToArray();
        bool hasContent = false;
        for (int i = 0; i < midRow.Length; i++)
            if (midRow[i] > 0) { hasContent = true; break; }
        await Assert.That(hasContent).IsTrue();
    }

    private static ImageFrame CreateGradientImage(int width, int height)
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
                row[off] = (ushort)(x * Quantum.MaxValue / Math.Max(width - 1, 1));
                row[off + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(height - 1, 1));
                row[off + 2] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(width + height - 2, 1));
            }
        }
        return image;
    }
}
