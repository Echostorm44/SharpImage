using SharpImage.Colorspaces;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Colorspace;

/// <summary>
/// Tests for image-level colorspace conversion operations (ColorspaceOps).
/// Verifies round-trip fidelity for all new colorspaces: Oklab, Oklch, JzAzBz, JzCzhz, DisplayP3, ProPhoto.
/// </summary>
public class ColorspaceOpsTests
{
    private static readonly string TestAssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string AssetPath(string name) => Path.Combine(TestAssetsDir, name);

    // ========== Round-trip image tests ==========

    [Test]
    [Arguments("oklab")]
    [Arguments("oklch")]
    [Arguments("jzazbz")]
    [Arguments("jzczhz")]
    [Arguments("displayp3")]
    [Arguments("prophoto")]
    [Arguments("hsl")]
    [Arguments("hsv")]
    [Arguments("hsi")]
    [Arguments("hwb")]
    [Arguments("hcl")]
    [Arguments("hclp")]
    [Arguments("xyz")]
    [Arguments("lab")]
    [Arguments("lchab")]
    [Arguments("luv")]
    [Arguments("lchuv")]
    [Arguments("lms")]
    [Arguments("ypbpr")]
    [Arguments("ycbcr")]
    [Arguments("yiq")]
    [Arguments("yuv")]
    [Arguments("ydbdr")]
    public async Task RoundTrip_PreservesImage(string colorspace)
    {
        using var source = FormatRegistry.Read(AssetPath("photo_small.png"));
        var result = ColorspaceOps.RoundTrip(source, colorspace);

        await Assert.That((int)result.Columns).IsEqualTo((int)source.Columns);
        await Assert.That((int)result.Rows).IsEqualTo((int)source.Rows);

        // Verify pixel-level fidelity: allow small error from quantization
        double maxError = 0;
        double totalError = 0;
        int pixelCount = 0;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        for (int y = 0; y < (int)source.Rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRow(y);
            for (int x = 0; x < (int)source.Columns; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                {
                    double diff = Math.Abs((double)srcRow[x * channels + c] - dstRow[x * channels + c]);
                    maxError = Math.Max(maxError, diff);
                    totalError += diff;
                    pixelCount++;
                }
            }
        }

        double avgError = totalError / pixelCount;

        // Round-trip through quantization should have max error < 2% of QuantumRange
        await Assert.That(maxError).IsLessThan(Quantum.MaxValue * 0.02)
            .Because($"{colorspace} max error ({maxError:F1}) should be < 2% of QuantumRange");
        await Assert.That(avgError).IsLessThan(Quantum.MaxValue * 0.005)
            .Because($"{colorspace} avg error ({avgError:F1}) should be < 0.5% of QuantumRange");
    }

    // ========== Convert forward produces different values ==========

    [Test]
    [Arguments("oklab")]
    [Arguments("oklch")]
    [Arguments("jzazbz")]
    [Arguments("jzczhz")]
    [Arguments("hsl")]
    [Arguments("hsv")]
    [Arguments("xyz")]
    [Arguments("lab")]
    [Arguments("ypbpr")]
    [Arguments("yuv")]
    public async Task ConvertTo_ProducesDifferentValues(string colorspace)
    {
        using var source = FormatRegistry.Read(AssetPath("photo_small.png"));
        var converted = ColorspaceOps.ConvertToColorspace(source, colorspace);

        // Converted image should have different pixel values than source
        int diffPixels = 0;
        int channels = source.NumberOfChannels;

        for (int y = 0; y < (int)source.Rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var cvtRow = converted.GetPixelRow(y);
            for (int x = 0; x < (int)source.Columns; x++)
            {
                int off = x * channels;
                if (srcRow[off] != cvtRow[off] || srcRow[off + 1] != cvtRow[off + 1] ||
                    srcRow[off + 2] != cvtRow[off + 2])
                    diffPixels++;
            }
        }

        // Most pixels should be different after colorspace conversion
        int totalPixels = (int)(source.Columns * source.Rows);
        await Assert.That(diffPixels).IsGreaterThan(totalPixels / 2);
    }

    // ========== Supported colorspaces list ==========

    [Test]
    public async Task SupportedColorspaces_ContainsAll23()
    {
        var supported = ColorspaceOps.SupportedColorspaces;
        await Assert.That(supported.Count).IsEqualTo(23);

        string[] expected = ["hsl", "hsv", "hsi", "hwb", "hcl", "hclp",
            "xyz", "lab", "lchab", "luv", "lchuv", "lms",
            "ypbpr", "ycbcr", "yiq", "yuv", "ydbdr",
            "oklab", "oklch", "jzazbz", "jzczhz",
            "displayp3", "prophoto"];
        foreach (string cs in expected)
        {
            await Assert.That(supported).Contains(cs);
        }
    }

    // ========== Invalid colorspace throws ==========

    [Test]
    public async Task ConvertTo_UnknownColorspace_Throws()
    {
        using var source = FormatRegistry.Read(AssetPath("photo_small.png"));
        await Assert.That(() => ColorspaceOps.ConvertToColorspace(source, "nonexistent"))
            .Throws<ArgumentException>();
    }

    // ========== Wide gamut: P3 and ProPhoto preserve sRGB content ==========

    [Test]
    public async Task DisplayP3_RoundTrip_PreservesWell()
    {
        using var source = FormatRegistry.Read(AssetPath("peppers_rgba.png"));
        var result = ColorspaceOps.RoundTrip(source, "displayp3");

        // P3 should have very low round-trip error for sRGB content
        double maxError = 0;
        int channels = source.NumberOfChannels;
        int colorChannels = Math.Min(channels, 3);

        for (int y = 0; y < (int)source.Rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = result.GetPixelRow(y);
            for (int x = 0; x < (int)source.Columns; x++)
            {
                for (int c = 0; c < colorChannels; c++)
                {
                    double diff = Math.Abs((double)srcRow[x * channels + c] - dstRow[x * channels + c]);
                    maxError = Math.Max(maxError, diff);
                }
            }
        }

        // Wide gamut round-trip through P3 should be very accurate
        await Assert.That(maxError).IsLessThan(Quantum.MaxValue * 0.02);
    }

    // ========== Oklab perceptual uniformity: gray should have a=b≈0.5 ==========

    [Test]
    public async Task Oklab_GrayPixel_HasNeutralAB()
    {
        // Create a small gray image
        var gray = new SharpImage.Image.ImageFrame();
        gray.Initialize(2, 2, ColorspaceType.SRGB, false);
        ushort mid = (ushort)(Quantum.MaxValue / 2);
        for (int y = 0; y < 2; y++)
        {
            var row = gray.GetPixelRowForWrite(y);
            for (int x = 0; x < 2; x++)
            {
                row[x * 3] = mid;
                row[x * 3 + 1] = mid;
                row[x * 3 + 2] = mid;
            }
        }

        var converted = ColorspaceOps.ConvertToColorspace(gray, "oklab");
        var row0 = converted.GetPixelRow(0);

        // a and b channels should be near QuantumMax/2 (neutral = 0 in raw Oklab)
        double aChannel = row0[1];
        double bChannel = row0[2];
        double halfMax = Quantum.MaxValue / 2.0;

        await Assert.That(Math.Abs(aChannel - halfMax)).IsLessThan(Quantum.MaxValue * 0.05);
        await Assert.That(Math.Abs(bChannel - halfMax)).IsLessThan(Quantum.MaxValue * 0.05);
    }
}
