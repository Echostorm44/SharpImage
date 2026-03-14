using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

/// <summary>
/// Tests for MathOps: Evaluate, Statistic, Function, Polynomial operations.
/// </summary>
public class MathOpsTests
{
    // ─── Evaluate ────────────────────────────────────────────────

    [Test]
    public async Task Evaluate_Add_IncreasesValues()
    {
        using var source = CreateSolid(16, 16, 100);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Add, 500);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(600);
    }

    [Test]
    public async Task Evaluate_Multiply_ScalesValues()
    {
        using var source = CreateSolid(16, 16, 1000);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Multiply, 2.0);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(2000);
    }

    [Test]
    public async Task Evaluate_Set_ReplacesAll()
    {
        using var source = CreateGradient(16, 16);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Set, 12345);

        var row = result.GetPixelRow(8);
        await Assert.That((int)row[0]).IsEqualTo(12345);
    }

    [Test]
    public async Task Evaluate_Min_ClampsHigh()
    {
        using var source = CreateSolid(16, 16, 30000);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Min, 20000);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(20000);
    }

    [Test]
    public async Task Evaluate_Max_ClampsLow()
    {
        using var source = CreateSolid(16, 16, 1000);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Max, 20000);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(20000);
    }

    [Test]
    public async Task Evaluate_Threshold_BinarizesImage()
    {
        using var source = CreateGradient(16, 16);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Threshold, Quantum.MaxValue / 2);

        var row = result.GetPixelRow(8);
        // All values should be either 0 or MaxValue
        bool allBinary = true;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] != 0 && row[i] != Quantum.MaxValue) { allBinary = false; break; }
        }

        await Assert.That(allBinary).IsTrue();
    }

    [Test]
    public async Task Evaluate_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var result = MathOps.Evaluate(source, EvaluateOperator.Add, 100);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    [Test]
    public async Task EvaluateImages_Mean_AveragesTwo()
    {
        using var black = CreateSolid(16, 16, 0);
        using var white = CreateSolid(16, 16, Quantum.MaxValue);
        ImageFrame[] images = [black, white];

        using var result = MathOps.EvaluateImages(images, EvaluateOperator.Mean);

        var row = result.GetPixelRow(0);
        // Mean of 0 and MaxValue ≈ MaxValue/2
        int expected = Quantum.MaxValue / 2;
        await Assert.That(Math.Abs(row[0] - expected)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task EvaluateImages_Min_TakesDarker()
    {
        using var dark = CreateSolid(16, 16, 1000);
        using var bright = CreateSolid(16, 16, 50000);
        ImageFrame[] images = [dark, bright];

        using var result = MathOps.EvaluateImages(images, EvaluateOperator.Min);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(1000);
    }

    [Test]
    public async Task EvaluateImages_Max_TakesBrighter()
    {
        using var dark = CreateSolid(16, 16, 1000);
        using var bright = CreateSolid(16, 16, 50000);
        ImageFrame[] images = [dark, bright];

        using var result = MathOps.EvaluateImages(images, EvaluateOperator.Max);

        var row = result.GetPixelRow(0);
        await Assert.That((int)row[0]).IsEqualTo(50000);
    }

    // ─── Statistic ───────────────────────────────────────────────

    [Test]
    public async Task Statistic_Median_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var result = MathOps.Statistic(source, StatisticType.Median, 1);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    [Test]
    public async Task Statistic_Minimum_FindsDarkest()
    {
        using var source = CreateStepEdge(16, 16);
        using var result = MathOps.Statistic(source, StatisticType.Minimum, 1);

        // At the step edge, the minimum in the 3x3 window should be 0
        var row = result.GetPixelRow(8);
        int channels = result.NumberOfChannels;
        // Pixels near the step edge (x=7,8) should be 0 due to minimum in window
        await Assert.That((int)row[7 * channels]).IsEqualTo(0);
    }

    [Test]
    public async Task Statistic_Maximum_FindsBrightest()
    {
        using var source = CreateStepEdge(16, 16);
        using var result = MathOps.Statistic(source, StatisticType.Maximum, 1);

        var row = result.GetPixelRow(8);
        int channels = result.NumberOfChannels;
        // Near the step, max in window should be MaxValue
        await Assert.That((int)row[8 * channels]).IsEqualTo(Quantum.MaxValue);
    }

    [Test]
    public async Task Statistic_Mean_SmoothsEdge()
    {
        using var source = CreateStepEdge(16, 16);
        using var result = MathOps.Statistic(source, StatisticType.Mean, 1);

        var srcRow = source.GetPixelRow(8);
        var dstRow = result.GetPixelRow(8);
        int channels = result.NumberOfChannels;
        // Mean should produce intermediate values at the edge
        int edgeVal = dstRow[8 * channels];
        await Assert.That(edgeVal).IsGreaterThan(0);
        await Assert.That(edgeVal).IsLessThan(Quantum.MaxValue);
    }

    [Test]
    public async Task Statistic_Gradient_HighAtEdge()
    {
        using var source = CreateStepEdge(16, 16);
        using var result = MathOps.Statistic(source, StatisticType.Gradient, 1);

        var row = result.GetPixelRow(8);
        int channels = result.NumberOfChannels;
        // Gradient (max-min) should be high at the step edge
        await Assert.That((int)row[8 * channels]).IsEqualTo(Quantum.MaxValue);
    }

    // ─── Function ────────────────────────────────────────────────

    [Test]
    public async Task Function_Polynomial_Identity()
    {
        using var source = CreateGradient(16, 16);
        // Polynomial [1, 0] = 1*x + 0 = identity
        using var result = MathOps.ApplyFunction(source, MathFunction.Polynomial, [1.0, 0.0]);

        var srcRow = source.GetPixelRow(8).ToArray();
        var dstRow = result.GetPixelRow(8).ToArray();

        // Should be very close to identity
        bool closeEnough = true;
        for (int i = 0; i < srcRow.Length; i++)
        {
            if (Math.Abs(srcRow[i] - dstRow[i]) > 1) { closeEnough = false; break; }
        }

        await Assert.That(closeEnough).IsTrue();
    }

    [Test]
    public async Task Function_Sinusoid_ProducesWave()
    {
        using var source = CreateGradient(64, 1);
        // Sinusoid: amplitude=0.5, freq=2, phase=0, bias=0.5
        using var result = MathOps.ApplyFunction(source, MathFunction.Sinusoid, [0.5, 2.0, 0.0, 0.5]);

        await Assert.That(result.Columns).IsEqualTo(64);
        // The wave should produce values between 0 and MaxValue (bias=0.5, amp=0.5 → 0..1)
        var row = result.GetPixelRow(0);
        bool inRange = true;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] < 0 || row[i] > Quantum.MaxValue) { inRange = false; break; }
        }

        await Assert.That(inRange).IsTrue();
    }

    [Test]
    public async Task Function_PreservesDimensions()
    {
        using var source = CreateGradient(24, 16);
        using var result = MathOps.ApplyFunction(source, MathFunction.Arctan, [1.0, 10.0, -5.0, 0.5]);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    // ─── Polynomial ──────────────────────────────────────────────

    [Test]
    public async Task Polynomial_EqualWeight_AveragesImages()
    {
        using var black = CreateSolid(16, 16, 0);
        using var white = CreateSolid(16, 16, Quantum.MaxValue);
        ImageFrame[] images = [black, white];
        // weight=0.5, exp=1 for both
        double[] terms = [0.5, 1.0, 0.5, 1.0];

        using var result = MathOps.Polynomial(images, terms);

        var row = result.GetPixelRow(0);
        int expected = Quantum.MaxValue / 2;
        await Assert.That(Math.Abs(row[0] - expected)).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Polynomial_PreservesDimensions()
    {
        using var img1 = CreateGradient(24, 16);
        ImageFrame[] images = [img1];
        double[] terms = [1.0, 1.0];

        using var result = MathOps.Polynomial(images, terms);

        await Assert.That(result.Columns).IsEqualTo(24);
        await Assert.That(result.Rows).IsEqualTo(16);
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static ImageFrame CreateSolid(int width, int height, ushort gray)
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
                row[off] = gray;
                row[off + 1] = gray;
                row[off + 2] = gray;
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

    private static ImageFrame CreateStepEdge(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = image.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (x < width / 2) ? (ushort)0 : Quantum.MaxValue;
                int off = x * channels;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return image;
    }
}
