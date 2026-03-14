// Unit tests for TensorExport — ML/AI tensor conversion.

using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using static SharpImage.Analysis.TensorExport;

namespace SharpImage.Tests.Analysis;

public class TensorExportTests
{
    private static readonly string TestAssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string Asset(string name) => Path.Combine(TestAssetsDir, name);

    [Test]
    public async Task ToTensor_CHW_Shape_Is_Correct()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(image, TensorLayout.CHW);
        var shape = TensorExport.GetShape(image, TensorLayout.CHW);

        await Assert.That(shape.Dim0).IsEqualTo(3);
        await Assert.That(shape.Dim1).IsEqualTo((int)image.Rows);
        await Assert.That(shape.Dim2).IsEqualTo((int)image.Columns);
        await Assert.That(tensor.Length).IsEqualTo(3 * (int)image.Rows * (int)image.Columns);
    }

    [Test]
    public async Task ToTensor_HWC_Shape_Is_Correct()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(image, TensorLayout.HWC);
        var shape = TensorExport.GetShape(image, TensorLayout.HWC);

        await Assert.That(shape.Dim0).IsEqualTo((int)image.Rows);
        await Assert.That(shape.Dim1).IsEqualTo((int)image.Columns);
        await Assert.That(shape.Dim2).IsEqualTo(3);
        await Assert.That(tensor.Length).IsEqualTo(3 * (int)image.Rows * (int)image.Columns);
    }

    [Test]
    public async Task ToTensor_ZeroToOne_Values_In_Range()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(image, normalization: NormalizationMode.ZeroToOne);

        float min = tensor.Min();
        float max = tensor.Max();
        await Assert.That(min).IsGreaterThanOrEqualTo(0f);
        await Assert.That(max).IsLessThanOrEqualTo(1.0f);
    }

    [Test]
    public async Task ToTensor_NegOneToOne_Values_In_Range()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(image, normalization: NormalizationMode.NegOneToOne);

        float min = tensor.Min();
        float max = tensor.Max();
        await Assert.That(min).IsGreaterThanOrEqualTo(-1.0f);
        await Assert.That(max).IsLessThanOrEqualTo(1.0f);
    }

    [Test]
    public async Task ToTensor_ImageNet_Normalization_Applied()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(image, normalization: NormalizationMode.ImageNet);

        // ImageNet normalization should produce values centered around 0
        float mean = tensor.Average();
        // Mean should be roughly near 0 (not exactly, depends on image)
        await Assert.That(mean).IsGreaterThan(-3.0f);
        await Assert.That(mean).IsLessThan(3.0f);
    }

    [Test]
    public async Task CHW_And_HWC_Contain_Same_Data()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var chw = TensorExport.ToTensor(image, TensorLayout.CHW);
        var hwc = TensorExport.ToTensor(image, TensorLayout.HWC);

        int h = (int)image.Rows, w = (int)image.Columns, c = 3;
        // Compare a few specific pixel values
        for (int ch = 0; ch < c; ch++)
        {
            for (int y = 0; y < Math.Min(5, h); y++)
            {
                for (int x = 0; x < Math.Min(5, w); x++)
                {
                    int chwIdx = ch * h * w + y * w + x;
                    int hwcIdx = y * w * c + x * c + ch;
                    await Assert.That(Math.Abs(chw[chwIdx] - hwc[hwcIdx])).IsLessThan(0.0001f);
                }
            }
        }
    }

    [Test]
    public async Task ToTensor3D_CHW_Dimensions_Correct()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor3d = TensorExport.ToTensor3D(image, TensorLayout.CHW);

        await Assert.That(tensor3d.Length).IsEqualTo(3); // channels
        await Assert.That(tensor3d[0].Length).IsEqualTo((int)image.Rows);
        await Assert.That(tensor3d[0][0].Length).IsEqualTo((int)image.Columns);
    }

    [Test]
    public async Task ToTensor3D_HWC_Dimensions_Correct()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor3d = TensorExport.ToTensor3D(image, TensorLayout.HWC);

        await Assert.That(tensor3d.Length).IsEqualTo((int)image.Rows); // height
        await Assert.That(tensor3d[0].Length).IsEqualTo((int)image.Columns); // width
        await Assert.That(tensor3d[0][0].Length).IsEqualTo(3); // channels
    }

    [Test]
    public async Task IncludeAlpha_Adds_Fourth_Channel()
    {
        using var image = FormatRegistry.Read(Asset("peppers_rgba.png")); // RGBA image
        var shape3 = TensorExport.GetShape(image, includeAlpha: false);
        var shape4 = TensorExport.GetShape(image, includeAlpha: true);

        await Assert.That(shape3.Dim0).IsEqualTo(3);
        await Assert.That(shape4.Dim0).IsEqualTo(4);
    }

    [Test]
    public async Task SaveBinary_And_LoadBinary_RoundTrip()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var tempFile = Path.GetTempFileName();
        try
        {
            TensorExport.SaveBinary(image, tempFile);
            var (data, dim0, dim1, dim2) = TensorExport.LoadBinary(tempFile);

            var shape = TensorExport.GetShape(image);
            await Assert.That(dim0).IsEqualTo(shape.Dim0);
            await Assert.That(dim1).IsEqualTo(shape.Dim1);
            await Assert.That(dim2).IsEqualTo(shape.Dim2);

            // Verify data matches
            var original = TensorExport.ToTensor(image);
            await Assert.That(data.Length).IsEqualTo(original.Length);

            for (int i = 0; i < Math.Min(100, data.Length); i++)
                await Assert.That(Math.Abs(data[i] - original[i])).IsLessThan(0.0001f);
        }
        finally { File.Delete(tempFile); }
    }

    [Test]
    public async Task FromTensor_Reconstructs_Image()
    {
        using var source = FormatRegistry.Read(Asset("photo_small.png"));
        var tensor = TensorExport.ToTensor(source, TensorLayout.CHW, NormalizationMode.ZeroToOne);
        var shape = TensorExport.GetShape(source);

        using var reconstructed = TensorExport.FromTensor(tensor, shape.Dim0, shape.Dim1, shape.Dim2, TensorLayout.CHW);

        await Assert.That(reconstructed.Columns).IsEqualTo(source.Columns);
        await Assert.That(reconstructed.Rows).IsEqualTo(source.Rows);

        // Verify pixel values are close (some quantization loss is expected)
        int srcChannels = source.HasAlpha ? 4 : 3;
        var srcSpan = source.GetPixelRow(0);
        var dstSpan = reconstructed.GetPixelRow(0);
        int checkLen = Math.Min(srcChannels * 5, srcSpan.Length);
        var srcVals = new ushort[checkLen];
        var dstVals = new ushort[checkLen];
        for (int i = 0; i < checkLen; i++)
        {
            srcVals[i] = srcSpan[i];
            dstVals[i] = dstSpan[i];
        }

        for (int i = 0; i < checkLen; i++)
        {
            int diff = Math.Abs(srcVals[i] - dstVals[i]);
            await Assert.That(diff).IsLessThan(256);
        }
    }

    [Test]
    public async Task GetChannelStatistics_Returns_Valid_Stats()
    {
        using var image = FormatRegistry.Read(Asset("photo_small.png"));
        var stats = TensorExport.GetChannelStatistics(image);

        await Assert.That(stats.Length).IsEqualTo(3);
        await Assert.That(stats[0].Name).IsEqualTo("Red");
        await Assert.That(stats[1].Name).IsEqualTo("Green");
        await Assert.That(stats[2].Name).IsEqualTo("Blue");

        foreach (var stat in stats)
        {
            await Assert.That(stat.Min).IsGreaterThanOrEqualTo(0f);
            await Assert.That(stat.Max).IsLessThanOrEqualTo(1.0f);
            await Assert.That(stat.Mean).IsGreaterThan(0f);
            await Assert.That(stat.Mean).IsLessThan(1.0f);
            await Assert.That(stat.Std).IsGreaterThan(0f);
        }
    }

    [Test]
    public async Task LoadBinary_Invalid_File_Throws()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19]);
            await Assert.That(() => TensorExport.LoadBinary(tempFile)).Throws<InvalidDataException>();
        }
        finally { File.Delete(tempFile); }
    }
}
