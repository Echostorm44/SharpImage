// Unit tests for PaletteExtraction — k-means dominant color extraction.

using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Analysis;

public class PaletteExtractionTests
{
    private static readonly string TestAssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string Asset(string name) => Path.Combine(TestAssetsDir, name);

    [Test]
    public async Task Extract_Returns_Requested_Color_Count()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 5);

        await Assert.That(palette.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Extract_Percentages_Sum_To_100()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 8);

        double totalPercentage = palette.Sum(p => p.Percentage);
        await Assert.That(Math.Abs(totalPercentage - 100.0)).IsLessThan(0.5);
    }

    [Test]
    public async Task Extract_Colors_In_Valid_Range()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image);

        foreach (var color in palette)
        {
            await Assert.That(color.R).IsGreaterThanOrEqualTo(0f);
            await Assert.That(color.R).IsLessThanOrEqualTo(1f);
            await Assert.That(color.G).IsGreaterThanOrEqualTo(0f);
            await Assert.That(color.G).IsLessThanOrEqualTo(1f);
            await Assert.That(color.B).IsGreaterThanOrEqualTo(0f);
            await Assert.That(color.B).IsLessThanOrEqualTo(1f);
        }
    }

    [Test]
    public async Task Extract_Sorted_By_Dominance_Descending()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image);

        for (int i = 1; i < palette.Length; i++)
            await Assert.That(palette[i - 1].Percentage).IsGreaterThanOrEqualTo(palette[i].Percentage);
    }

    [Test]
    public async Task Extract_Red_Image_Has_Red_Dominant()
    {
        // Create a solid red image
        var redFrame = new ImageFrame();
        redFrame.Initialize(100, 100, ColorspaceType.SRGB, false);
        for (int y = 0; y < 100; y++)
        {
            var row = redFrame.GetPixelRowForWrite(y);
            for (int x = 0; x < 100; x++)
            {
                row[x * 3] = Quantum.MaxValue;     // R
                row[x * 3 + 1] = 0;                 // G
                row[x * 3 + 2] = 0;                 // B
            }
        }

        var palette = PaletteExtraction.Extract(redFrame, colorCount: 3);

        // First color should be pure red
        await Assert.That(palette[0].R).IsGreaterThan(0.9f);
        await Assert.That(palette[0].G).IsLessThan(0.1f);
        await Assert.That(palette[0].B).IsLessThan(0.1f);
        await Assert.That(palette[0].Percentage).IsGreaterThan(90.0);

        redFrame.Dispose();
    }

    [Test]
    public async Task ToHex_Returns_Correct_Format()
    {
        var color = new PaletteExtraction.PaletteColor(1.0f, 0.5f, 0.0f, 50.0);
        string hex = color.ToHex();

        await Assert.That(hex).IsEqualTo("#FF8000");
    }

    [Test]
    public async Task RenderSwatch_Creates_Correct_Size()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 5);
        using var swatch = PaletteExtraction.RenderSwatch(palette, 400, 80);

        await Assert.That((int)swatch.Columns).IsEqualTo(400);
        await Assert.That((int)swatch.Rows).IsEqualTo(80);
    }

    [Test]
    public async Task RenderSwatch_First_Band_Matches_Dominant_Color()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 5);
        using var swatch = PaletteExtraction.RenderSwatch(palette, 400, 80);

        // First pixel should match the most dominant color
        var row = swatch.GetPixelRow(40); // middle row
        ushort r = row[0], g = row[1], b = row[2];

        await Assert.That(r).IsEqualTo(palette[0].QuantumR);
        await Assert.That(g).IsEqualTo(palette[0].QuantumG);
        await Assert.That(b).IsEqualTo(palette[0].QuantumB);
    }

    [Test]
    public async Task MapToPalette_Output_Uses_Only_Palette_Colors()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 4);
        using var mapped = PaletteExtraction.MapToPalette(image, palette);

        await Assert.That((int)mapped.Columns).IsEqualTo((int)image.Columns);
        await Assert.That((int)mapped.Rows).IsEqualTo((int)image.Rows);

        // Check a few pixels are in the palette
        var span = mapped.GetPixelRow(20);
        int channels = mapped.HasAlpha ? 4 : 3;
        int checkCount = Math.Min(10, (int)mapped.Columns);
        var pixelColors = new (ushort R, ushort G, ushort B)[checkCount];
        for (int x = 0; x < checkCount; x++)
            pixelColors[x] = (span[x * channels], span[x * channels + 1], span[x * channels + 2]);

        for (int x = 0; x < checkCount; x++)
        {
            var (r, g, b) = pixelColors[x];
            bool found = palette.Any(p => p.QuantumR == r && p.QuantumG == g && p.QuantumB == b);
            await Assert.That(found).IsTrue();
        }
    }

    [Test]
    public async Task Extract_WithAlpha_Image_Still_Works()
    {
        using var image = FormatRegistry.Read(Asset("peppers_rgba.png")); // RGBA
        var palette = PaletteExtraction.Extract(image, colorCount: 6);

        await Assert.That(palette.Length).IsEqualTo(6);
        double total = palette.Sum(p => p.Percentage);
        await Assert.That(Math.Abs(total - 100.0)).IsLessThan(0.5);
    }

    [Test]
    public async Task MapToPalette_Preserves_Alpha()
    {
        using var image = FormatRegistry.Read(Asset("peppers_rgba.png")); // RGBA
        var palette = PaletteExtraction.Extract(image, colorCount: 4);
        using var mapped = PaletteExtraction.MapToPalette(image, palette);

        await Assert.That(mapped.HasAlpha).IsTrue();

        // Check alpha is preserved
        var srcRow = image.GetPixelRow(0);
        var dstRow = mapped.GetPixelRow(0);
        int srcCh = image.HasAlpha ? 4 : 3;
        int dstCh = mapped.HasAlpha ? 4 : 3;

        // Copy out values before await
        var srcAlphas = new ushort[5];
        var dstAlphas = new ushort[5];
        for (int x = 0; x < 5; x++)
        {
            srcAlphas[x] = srcRow[x * srcCh + 3];
            dstAlphas[x] = dstRow[x * dstCh + 3];
        }

        for (int x = 0; x < 5; x++)
            await Assert.That(dstAlphas[x]).IsEqualTo(srcAlphas[x]);
    }

    [Test]
    public async Task Extract_Large_K_Handles_Gracefully()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var palette = PaletteExtraction.Extract(image, colorCount: 32);

        await Assert.That(palette.Length).IsEqualTo(32);
    }
}
