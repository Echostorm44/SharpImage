// Unit tests for the ImagePipeline fluent API.

using SharpImage;
using SharpImage.Effects;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests;

public class ImagePipelineTests
{
    private static readonly string TestAssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string Asset(string name) => Path.Combine(TestAssetsDir, name);

    [Test]
    public async Task Load_And_Save_RoundTrips()
    {
        var outPath = Path.GetTempFileName() + ".png";
        try
        {
            using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
            pipeline.Save(outPath);
            using var result = FormatRegistry.Read(outPath);
            await Assert.That(result.Columns).IsGreaterThan(0);
            await Assert.That(result.Rows).IsGreaterThan(0);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Test]
    public async Task From_Clones_Source()
    {
        using var source = FormatRegistry.Read(Asset("photo_small.png"));
        using var pipeline = ImagePipeline.From(source);
        var result = pipeline.Grayscale().ToFrame();
        // Source should be unchanged (still has color)
        var srcRow = source.GetPixelRow(0);
        await Assert.That(srcRow[0] != srcRow[1] || srcRow[1] != srcRow[2]).IsTrue();
        result.Dispose();
    }

    [Test]
    public async Task Single_Operation_Resize()
    {
        using var pipeline = ImagePipeline.Load(Asset("peppers_rgba.png"));
        var (w, h) = pipeline.Resize(200, 300).Size;
        await Assert.That(w).IsEqualTo(200);
        await Assert.That(h).IsEqualTo(300);
    }

    [Test]
    public async Task Chained_Operations_Execute_In_Order()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var result = pipeline
            .Resize(50, 50)
            .Grayscale()
            .Invert()
            .ToFrame();

        await Assert.That(result.Columns).IsEqualTo(50);
        await Assert.That(result.Rows).IsEqualTo(50);
        // Verify grayscale (R == G == B for all pixels)
        var row = result.GetPixelRow(25);
        ushort r = row[0], g = row[1], b = row[2];
        await Assert.That(r).IsEqualTo(g);
        await Assert.That(g).IsEqualTo(b);
        result.Dispose();
    }

    [Test]
    public async Task Multi_Step_Pipeline_Produces_Valid_Output()
    {
        var outPath = Path.GetTempFileName() + ".png";
        try
        {
            using var pipeline = ImagePipeline.Load(Asset("peppers_rgba.png"));
            pipeline
                .Resize(400, 500)
                .GaussianBlur(1.5)
                .Brightness(1.1)
                .Contrast(1.2)
                .SepiaTone(0.8)
                .Save(outPath);

            using var result = FormatRegistry.Read(outPath);
            await Assert.That(result.Columns).IsEqualTo(400);
            await Assert.That(result.Rows).IsEqualTo(500);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Test]
    public async Task Flip_And_Flop()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var result = pipeline.Flip().Flop().ToFrame();
        await Assert.That(result.Columns).IsGreaterThan(0);
        result.Dispose();
    }

    [Test]
    public async Task Crop_Produces_Correct_Size()
    {
        using var pipeline = ImagePipeline.Load(Asset("peppers_rgba.png"));
        var (w, h) = pipeline.Crop(10, 10, 100, 200).Size;
        await Assert.That(w).IsEqualTo(100);
        await Assert.That(h).IsEqualTo(200);
    }

    [Test]
    public async Task Artistic_Operations_Chain()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var result = pipeline
            .Resize(64, 64)
            .OilPaint(2)
            .Swirl(30.0)
            .ToFrame();

        await Assert.That(result.Columns).IsEqualTo(64);
        await Assert.That(result.Rows).IsEqualTo(64);
        result.Dispose();
    }

    [Test]
    public async Task Enhancement_Operations_Chain()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var result = pipeline
            .Equalize()
            .AutoLevel()
            .Normalize()
            .ToFrame();

        await Assert.That(result.Columns).IsGreaterThan(0);
        result.Dispose();
    }

    [Test]
    public async Task Encode_Returns_Bytes()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var bytes = pipeline.Resize(32, 32).Encode(ImageFileFormat.Png);
        await Assert.That(bytes.Length).IsGreaterThan(0);
        // PNG signature
        await Assert.That(bytes[0]).IsEqualTo((byte)0x89);
        await Assert.That(bytes[1]).IsEqualTo((byte)0x50);
    }

    [Test]
    public async Task Inspect_Does_Not_Modify()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        long capturedWidth = 0;
        pipeline.Inspect(f => capturedWidth = f.Columns).Resize(32, 32);
        var (w, _) = pipeline.Size;
        await Assert.That(capturedWidth).IsGreaterThan(32);
        await Assert.That(w).IsEqualTo(32);
    }

    [Test]
    public async Task Custom_Apply_Works()
    {
        using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        var result = pipeline.Apply(f => ColorAdjust.Grayscale(f)).ToFrame();
        var row = result.GetPixelRow(0);
        int channels = result.HasAlpha ? 4 : 3;
        await Assert.That(row[0]).IsEqualTo(row[1]);
        result.Dispose();
    }

    [Test]
    public async Task Dispose_Prevents_Further_Use()
    {
        var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
        pipeline.Dispose();
        await Assert.That(() => { _ = pipeline.Size; }).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task Text_Step_Parsing_Works()
    {
        using var pipeline = ImagePipeline.Execute(Asset("photo_small.png"), ["resize 50 50", "grayscale", "invert"]);
        var (w, h) = pipeline.Size;
        await Assert.That(w).IsEqualTo(50);
        await Assert.That(h).IsEqualTo(50);
    }

    [Test]
    public async Task Text_Step_Unknown_Op_Throws()
    {
        await Assert.That(() => ImagePipeline.Execute(Asset("photo_small.png"), ["unknownop"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Save_Returns_Pipeline_For_Continued_Use()
    {
        var outPath1 = Path.GetTempFileName() + ".png";
        var outPath2 = Path.GetTempFileName() + ".png";
        try
        {
            using var pipeline = ImagePipeline.Load(Asset("photo_small.png"));
            pipeline
                .Resize(100, 100)
                .Save(outPath1)
                .Grayscale()
                .Save(outPath2);

            using var result1 = FormatRegistry.Read(outPath1);
            using var result2 = FormatRegistry.Read(outPath2);
            await Assert.That(result1.Columns).IsEqualTo(100);
            await Assert.That(result2.Columns).IsEqualTo(100);
        }
        finally
        {
            if (File.Exists(outPath1)) File.Delete(outPath1);
            if (File.Exists(outPath2)) File.Delete(outPath2);
        }
    }
}
