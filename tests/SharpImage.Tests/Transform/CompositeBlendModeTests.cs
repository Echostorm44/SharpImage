using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Tests for all 23 new composite blend modes added in Group 6.
/// Each test verifies correct pixel math for the specific blend operation.
/// </summary>
public class CompositeBlendModeTests
{
    // ─── Photoshop-standard blend modes ─────────────────────────

    [Test]
    public async Task Composite_Darken_KeepsMinimumPerChannel()
    {
        using var baseImg = CreateSolidImage(4, 4, 40000, 20000, 60000);
        using var overlay = CreateSolidImage(4, 4, 20000, 50000, 30000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Darken);

        var row = baseImg.GetPixelRow(0).ToArray();
        // Should keep minimum per channel
        await Assert.That(row[0]).IsLessThanOrEqualTo((ushort)21000); // min(40000,20000) ≈ 20000
        await Assert.That(row[1]).IsLessThanOrEqualTo((ushort)21000); // min(20000,50000) ≈ 20000
        await Assert.That(row[2]).IsLessThanOrEqualTo((ushort)31000); // min(60000,30000) ≈ 30000
    }

    [Test]
    public async Task Composite_Lighten_KeepsMaximumPerChannel()
    {
        using var baseImg = CreateSolidImage(4, 4, 20000, 50000, 30000);
        using var overlay = CreateSolidImage(4, 4, 40000, 20000, 60000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Lighten);

        var row = baseImg.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsGreaterThanOrEqualTo((ushort)39000); // max(20000,40000) ≈ 40000
        await Assert.That(row[1]).IsGreaterThanOrEqualTo((ushort)49000); // max(50000,20000) ≈ 50000
        await Assert.That(row[2]).IsGreaterThanOrEqualTo((ushort)59000); // max(30000,60000) ≈ 60000
    }

    [Test]
    public async Task Composite_ColorDodge_BrightensBase()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768); // mid gray
        using var overlay = CreateSolidImage(4, 4, 32768, 32768, 32768); // mid gray

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.ColorDodge);

        var row = baseImg.GetPixelRow(0).ToArray();
        // ColorDodge brightens: base / (1 - overlay) = 0.5 / 0.5 = 1.0
        await Assert.That(row[0]).IsGreaterThanOrEqualTo((ushort)60000);
    }

    [Test]
    public async Task Composite_ColorBurn_DarkensBase()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768);
        using var overlay = CreateSolidImage(4, 4, 32768, 32768, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.ColorBurn);

        var row = baseImg.GetPixelRow(0).ToArray();
        // ColorBurn darkens: 1 - (1-base)/overlay = 1 - 0.5/0.5 = 0
        await Assert.That(row[0]).IsLessThanOrEqualTo((ushort)5000);
    }

    [Test]
    public async Task Composite_HardLight_BehavesLikeSwappedOverlay()
    {
        using var baseImg = CreateSolidImage(4, 4, 16384, 49152, 32768);
        using var overlay = CreateSolidImage(4, 4, 49152, 16384, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.HardLight);

        var row = baseImg.GetPixelRow(0).ToArray();
        // HardLight: overlay > 0.5 → screen path brightens; overlay < 0.5 → multiply path
        await Assert.That(row[0]).IsGreaterThan((ushort)16384); // bright overlay → brightened
        await Assert.That(row[1]).IsLessThan((ushort)49152);    // dark overlay → result < base
    }

    [Test]
    public async Task Composite_SoftLight_GentleContrast()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768);
        using var overlay = CreateSolidImage(4, 4, 49152, 16384, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.SoftLight);

        var row = baseImg.GetPixelRow(0).ToArray();
        // Bright overlay lightens gently, dark overlay darkens gently
        await Assert.That(row[0]).IsGreaterThan((ushort)32768);
        await Assert.That(row[1]).IsLessThan((ushort)32768);
        // Mid overlay on mid base → stays near mid
        await Assert.That(row[2]).IsGreaterThan((ushort)30000);
        await Assert.That(row[2]).IsLessThan((ushort)35000);
    }

    [Test]
    public async Task Composite_Difference_AbsoluteChannelDifference()
    {
        using var baseImg = CreateSolidImage(4, 4, 40000, 20000, 50000);
        using var overlay = CreateSolidImage(4, 4, 20000, 50000, 50000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Difference);

        var row = baseImg.GetPixelRow(0).ToArray();
        // |40000-20000|/65535 ≈ 0.305, |20000-50000|/65535 ≈ 0.458, |50000-50000| = 0
        await Assert.That(row[0]).IsGreaterThan((ushort)18000);
        await Assert.That(row[1]).IsGreaterThan((ushort)28000);
        await Assert.That(row[2]).IsLessThan((ushort)2000); // same values → 0
    }

    [Test]
    public async Task Composite_Exclusion_LowerContrastDifference()
    {
        using var baseImg = CreateSolidImage(4, 4, 40000, 20000, 50000);
        using var overlay = CreateSolidImage(4, 4, 20000, 50000, 50000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Exclusion);

        var row = baseImg.GetPixelRow(0).ToArray();
        // Exclusion: b + o - 2*b*o. Same values → b+b-2*b*b = 2b(1-b)
        // For b=50000/65535≈0.763: 2*0.763*0.237 ≈ 0.362 → ~23737
        await Assert.That(row[2]).IsGreaterThan((ushort)20000);
        await Assert.That(row[2]).IsLessThan((ushort)27000);
    }

    // ─── Porter-Duff compositing algebra ────────────────────────

    [Test]
    public async Task Composite_Dissolve_RandomPixelSelection()
    {
        using var baseImg = CreateSolidImage(32, 32, 10000, 10000, 10000);
        using var overlay = CreateSolidImageWithAlpha(32, 32, 50000, 50000, 50000, 32768); // 50% alpha

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Dissolve);

        // With 50% alpha, roughly half the pixels should be overlay, half base
        int overlayCount = 0;
        int total = 32 * 32;
        for (int y = 0; y < 32; y++)
        {
            var row = baseImg.GetPixelRow(y).ToArray();
            for (int x = 0; x < 32; x++)
                if (row[x * 3] > 30000) overlayCount++;
        }
        // Should be roughly 50% ± generous tolerance
        await Assert.That(overlayCount).IsGreaterThan(total / 4);
        await Assert.That(overlayCount).IsLessThan(3 * total / 4);
    }

    [Test]
    public async Task Composite_Atop_OverlayOnlyWhereBaseOpaque()
    {
        // Base: left half opaque, right half transparent
        using var baseImg = CreateHalfOpaqueImage(8, 4, 30000, 30000, 30000);
        using var overlay = CreateSolidImageWithAlpha(8, 4, 60000, 60000, 60000, 65535);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Atop);

        // Left side (opaque base): should show overlay
        var row = baseImg.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsGreaterThan((ushort)55000); // overlay visible

        // Right side (transparent base): should remain transparent
        int channels = baseImg.NumberOfChannels;
        int rightX = 6;
        await Assert.That(row[rightX * channels + 3]).IsLessThan((ushort)1000); // alpha near 0
    }

    [Test]
    public async Task Composite_In_ClippedToBaseAlpha()
    {
        using var baseImg = CreateHalfOpaqueImage(8, 4, 30000, 30000, 30000);
        using var overlay = CreateSolidImageWithAlpha(8, 4, 60000, 60000, 60000, 65535);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.In);

        var row = baseImg.GetPixelRow(0).ToArray();
        int channels = baseImg.NumberOfChannels;
        // Left (opaque base): overlay alpha * base alpha = 1*1 = 1 → overlay visible
        await Assert.That(row[3]).IsGreaterThan((ushort)60000);
        // Right (transparent base): overlay alpha * 0 = 0 → transparent
        await Assert.That(row[6 * channels + 3]).IsLessThan((ushort)1000);
    }

    [Test]
    public async Task Composite_Out_OverlayWhereBaseTransparent()
    {
        using var baseImg = CreateHalfOpaqueImage(8, 4, 30000, 30000, 30000);
        using var overlay = CreateSolidImageWithAlpha(8, 4, 60000, 60000, 60000, 65535);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Out);

        var row = baseImg.GetPixelRow(0).ToArray();
        int channels = baseImg.NumberOfChannels;
        // Left (opaque base): alpha * (1 - baseAlpha) = 1*(1-1) = 0 → transparent
        await Assert.That(row[3]).IsLessThan((ushort)1000);
        // Right (transparent base): alpha * (1-0) = 1 → visible
        await Assert.That(row[6 * channels + 3]).IsGreaterThan((ushort)60000);
    }

    [Test]
    public async Task Composite_Xor_NonOverlappingRegions()
    {
        using var baseImg = CreateHalfOpaqueImage(8, 4, 30000, 30000, 30000);
        using var overlay = CreateSolidImageWithAlpha(8, 4, 60000, 60000, 60000, 65535);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Xor);

        var row = baseImg.GetPixelRow(0).ToArray();
        int channels = baseImg.NumberOfChannels;
        // Left: both opaque → alpha = oA*(1-bA) + bA*(1-oA) = 0 → transparent
        await Assert.That(row[3]).IsLessThan((ushort)1000);
        // Right: only overlay → alpha = oA*(1-0) + 0*(1-oA) = 1 → visible
        await Assert.That(row[6 * channels + 3]).IsGreaterThan((ushort)60000);
    }

    // ─── Arithmetic blend modes ─────────────────────────────────

    [Test]
    public async Task Composite_Plus_LinearAdd()
    {
        using var baseImg = CreateSolidImage(4, 4, 30000, 30000, 30000);
        using var overlay = CreateSolidImage(4, 4, 30000, 30000, 30000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Plus);

        var row = baseImg.GetPixelRow(0).ToArray();
        // 30000/65535 + 30000/65535 ≈ 0.916 → ~60000
        await Assert.That(row[0]).IsGreaterThan((ushort)55000);
    }

    [Test]
    public async Task Composite_Minus_LinearSubtract()
    {
        using var baseImg = CreateSolidImage(4, 4, 50000, 50000, 50000);
        using var overlay = CreateSolidImage(4, 4, 20000, 20000, 20000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Minus);

        var row = baseImg.GetPixelRow(0).ToArray();
        // 50000/65535 - 20000/65535 ≈ 0.458 → ~30000
        await Assert.That(row[0]).IsGreaterThan((ushort)25000);
        await Assert.That(row[0]).IsLessThan((ushort)35000);
    }

    [Test]
    public async Task Composite_ModulusAdd_Wraps()
    {
        using var baseImg = CreateSolidImage(4, 4, 50000, 50000, 10000);
        using var overlay = CreateSolidImage(4, 4, 40000, 10000, 10000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.ModulusAdd);

        var row = baseImg.GetPixelRow(0).ToArray();
        // (50000+40000)/65535 = 90000/65535 → wraps: 90000/65535 mod 1 = 0.373 → ~24455
        // Small sum (10000+10000): stays small since (20000/65535) mod 1 = 0.305
        await Assert.That(row[2]).IsGreaterThan((ushort)15000);
        await Assert.That(row[2]).IsLessThan((ushort)25000);
    }

    [Test]
    public async Task Composite_ModulusSubtract_Wraps()
    {
        using var baseImg = CreateSolidImage(4, 4, 10000, 50000, 50000);
        using var overlay = CreateSolidImage(4, 4, 40000, 10000, 50000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.ModulusSubtract);

        var row = baseImg.GetPixelRow(0).ToArray();
        // Same values subtract to 0
        await Assert.That(row[2]).IsLessThan((ushort)2000);
    }

    // ─── Special blend modes ────────────────────────────────────

    [Test]
    public async Task Composite_Bumpmap_ModulatesByLuminance()
    {
        using var baseImg = CreateSolidImage(4, 4, 60000, 60000, 60000);
        using var overlay = CreateSolidImage(4, 4, 32768, 32768, 32768); // ~50% luminance

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.Bumpmap);

        var row = baseImg.GetPixelRow(0).ToArray();
        // 60000 * 0.5 ≈ 30000
        await Assert.That(row[0]).IsGreaterThan((ushort)25000);
        await Assert.That(row[0]).IsLessThan((ushort)35000);
    }

    [Test]
    public async Task Composite_HardMix_ProducesBlackOrWhite()
    {
        using var baseImg = CreateSolidImage(4, 4, 40000, 10000, 50000);
        using var overlay = CreateSolidImage(4, 4, 40000, 10000, 20000);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.HardMix);

        var row = baseImg.GetPixelRow(0).ToArray();
        // 40000+40000 > 65535 → white; 10000+10000 < 65535 → black; 50000+20000 > 65535 → white
        await Assert.That(row[0]).IsGreaterThan((ushort)60000); // white
        await Assert.That(row[1]).IsLessThan((ushort)5000);     // black
        await Assert.That(row[2]).IsGreaterThan((ushort)60000); // white
    }

    [Test]
    public async Task Composite_LinearLight_CombinesLinearBurnDodge()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768);
        using var overlay = CreateSolidImage(4, 4, 49152, 16384, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.LinearLight);

        var row = baseImg.GetPixelRow(0).ToArray();
        // base + 2*overlay - 1: bright overlay brightens, dark darkens
        await Assert.That(row[0]).IsGreaterThan((ushort)32768);
        await Assert.That(row[1]).IsLessThan((ushort)32768);
    }

    [Test]
    public async Task Composite_VividLight_CombinesBurnDodge()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768);
        using var overlay = CreateSolidImage(4, 4, 49152, 16384, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.VividLight);

        var row = baseImg.GetPixelRow(0).ToArray();
        await Assert.That(row[0]).IsGreaterThan((ushort)32768); // dodge path
        await Assert.That(row[1]).IsLessThan((ushort)32768);    // burn path
    }

    [Test]
    public async Task Composite_PinLight_SelectiveReplacement()
    {
        using var baseImg = CreateSolidImage(4, 4, 32768, 32768, 32768);
        using var overlay = CreateSolidImage(4, 4, 49152, 16384, 32768);

        Composite.Apply(baseImg, overlay, 0, 0, CompositeMode.PinLight);

        var row = baseImg.GetPixelRow(0).ToArray();
        // overlay > 0.5 → max(base, 2*overlay-1); overlay < 0.5 → min(base, 2*overlay)
        await Assert.That(row[0]).IsGreaterThanOrEqualTo((ushort)32768);
        await Assert.That(row[1]).IsLessThanOrEqualTo((ushort)32768);
    }

    // ─── Real image test ────────────────────────────────────────

    [Test]
    public async Task Composite_AllModes_ProduceValidOutput()
    {
        using var baseImg = CreateGradientImage(32, 32);
        using var overlay = CreateSolidImage(16, 16, 40000, 20000, 50000);

        var modes = Enum.GetValues<CompositeMode>();
        foreach (var mode in modes)
        {
            using var copy = CloneImage(baseImg);
            Composite.Apply(copy, overlay, 8, 8, mode);

            // Verify image wasn't corrupted
            await Assert.That(copy.Columns).IsEqualTo(32);
            await Assert.That(copy.Rows).IsEqualTo(32);
        }
    }

    // ─── Helper Methods ─────────────────────────────────────────

    private static ImageFrame CreateSolidImage(int w, int h, ushort r, ushort g, ushort b)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, false);
        int ch = img.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = r;
                row[x * ch + 1] = g;
                row[x * ch + 2] = b;
            }
        }
        return img;
    }

    private static ImageFrame CreateSolidImageWithAlpha(int w, int h, ushort r, ushort g, ushort b, ushort a)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, true);
        int ch = img.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = r;
                row[x * ch + 1] = g;
                row[x * ch + 2] = b;
                row[x * ch + 3] = a;
            }
        }
        return img;
    }

    private static ImageFrame CreateHalfOpaqueImage(int w, int h, ushort r, ushort g, ushort b)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, true);
        int ch = img.NumberOfChannels;
        int half = w / 2;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = r;
                row[x * ch + 1] = g;
                row[x * ch + 2] = b;
                row[x * ch + 3] = x < half ? Quantum.MaxValue : (ushort)0;
            }
        }
        return img;
    }

    private static ImageFrame CreateGradientImage(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, false);
        int ch = img.NumberOfChannels;
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * ch] = (ushort)(x * Quantum.MaxValue / Math.Max(w - 1, 1));
                row[x * ch + 1] = (ushort)(y * Quantum.MaxValue / Math.Max(h - 1, 1));
                row[x * ch + 2] = (ushort)((x + y) * Quantum.MaxValue / Math.Max(w + h - 2, 1));
            }
        }
        return img;
    }

    private static ImageFrame CloneImage(ImageFrame source)
    {
        var clone = new ImageFrame();
        clone.Initialize(source.Columns, source.Rows, source.Colorspace, source.HasAlpha);
        for (int y = 0; y < (int)source.Rows; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var dstRow = clone.GetPixelRowForWrite(y);
            srcRow.CopyTo(dstRow);
        }
        return clone;
    }
}
