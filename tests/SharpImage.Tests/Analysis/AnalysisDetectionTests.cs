using SharpImage.Analysis;
using SharpImage.Core;
using SharpImage.Image;
using TUnit.Core;

namespace SharpImage.Tests.Analysis;

/// <summary>
/// Tests for Group 7: Analysis &amp; Detection features —
/// PerceptualHash, CannyEdge, HoughLines, ConnectedComponents, MeanShift.
/// </summary>
public class AnalysisDetectionTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Perceptual Hash Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task PerceptualHash_IdenticalImages_ZeroDistance()
    {
        using var img = CreateGradient(64, 64);
        ulong hash1 = PerceptualHash.Compute(img);
        ulong hash2 = PerceptualHash.Compute(img);

        await Assert.That(hash1).IsEqualTo(hash2);
        await Assert.That(PerceptualHash.HammingDistance(hash1, hash2)).IsEqualTo(0);
    }

    [Test]
    public async Task PerceptualHash_SimilarImages_LowDistance()
    {
        using var img = CreateGradient(64, 64);
        using var slightlyDifferent = CreateGradient(64, 64);

        // Apply a subtle brightness shift — structurally similar but not identical
        for (int y = 0; y < 64; y++)
        {
            var row = slightlyDifferent.GetPixelRowForWrite(y);
            for (int x = 0; x < 64; x++)
            {
                int off = x * 3;
                row[off] = (ushort)Math.Min(row[off] + 500, Quantum.MaxValue);
                row[off + 1] = (ushort)Math.Min(row[off + 1] + 500, Quantum.MaxValue);
                row[off + 2] = (ushort)Math.Min(row[off + 2] + 500, Quantum.MaxValue);
            }
        }

        ulong hash1 = PerceptualHash.Compute(img);
        ulong hash2 = PerceptualHash.Compute(slightlyDifferent);

        int distance = PerceptualHash.HammingDistance(hash1, hash2);
        // A subtle brightness shift should produce a small Hamming distance
        await Assert.That(distance).IsLessThanOrEqualTo(20);
    }

    [Test]
    public async Task PerceptualHash_VeryDifferentImages_HighDistance()
    {
        using var black = CreateSolid(64, 64, 0, 0, 0);
        using var noisy = CreateRandom(64, 64, 42);

        ulong hash1 = PerceptualHash.Compute(black);
        ulong hash2 = PerceptualHash.Compute(noisy);

        int distance = PerceptualHash.HammingDistance(hash1, hash2);
        await Assert.That(distance).IsGreaterThan(10);
    }

    [Test]
    public async Task PerceptualHash_AreSimilar_WorksCorrectly()
    {
        using var img = CreateGradient(32, 32);
        ulong hash = PerceptualHash.Compute(img);

        await Assert.That(PerceptualHash.AreSimilar(hash, hash)).IsTrue();
        await Assert.That(PerceptualHash.AreSimilar(hash, ~hash, threshold: 0)).IsFalse();
    }

    [Test]
    public async Task PerceptualHash_HexRoundTrip()
    {
        using var img = CreateGradient(32, 32);
        ulong hash = PerceptualHash.Compute(img);

        string hex = PerceptualHash.ToHexString(hash);
        ulong parsed = PerceptualHash.FromHexString(hex);

        await Assert.That(hex.Length).IsEqualTo(16);
        await Assert.That(parsed).IsEqualTo(hash);
    }

    [Test]
    public async Task PerceptualHash_DifferentSizes_StillWorks()
    {
        using var small = CreateGradient(16, 16);
        using var large = CreateGradient(256, 256);

        ulong hash1 = PerceptualHash.Compute(small);
        ulong hash2 = PerceptualHash.Compute(large);

        // Same gradient content at different sizes should produce similar hashes
        int distance = PerceptualHash.HammingDistance(hash1, hash2);
        await Assert.That(distance).IsLessThanOrEqualTo(15);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Canny Edge Detection Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task CannyEdge_SolidImage_NoEdges()
    {
        using var solid = CreateSolid(32, 32, 128, 128, 128);
        using var edges = CannyEdge.Detect(solid);

        // Solid image should produce no edges
        int edgePixels = CountWhitePixels(edges);
        await Assert.That(edgePixels).IsEqualTo(0);
    }

    [Test]
    public async Task CannyEdge_BlackWhiteEdge_DetectsEdge()
    {
        // Create image with sharp left-right boundary
        using var img = CreateHalfImage(64, 64);
        using var edges = CannyEdge.Detect(img, sigma: 1.0, lowThreshold: 0.05, highThreshold: 0.15);

        int edgePixels = CountWhitePixels(edges);
        await Assert.That(edgePixels).IsGreaterThan(0);
    }

    [Test]
    public async Task CannyEdge_OutputDimensions_MatchInput()
    {
        using var img = CreateGradient(48, 32);
        using var edges = CannyEdge.Detect(img);

        await Assert.That((int)edges.Columns).IsEqualTo(48);
        await Assert.That((int)edges.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task CannyEdge_HighThreshold_FewerEdges()
    {
        using var img = CreateCheckerboard(32, 32, 4);
        using var edgesLow = CannyEdge.Detect(img, sigma: 1.0, lowThreshold: 0.01, highThreshold: 0.05);
        using var edgesHigh = CannyEdge.Detect(img, sigma: 1.0, lowThreshold: 0.1, highThreshold: 0.5);

        int lowCount = CountWhitePixels(edgesLow);
        int highCount = CountWhitePixels(edgesHigh);

        await Assert.That(highCount).IsLessThanOrEqualTo(lowCount);
    }

    [Test]
    public async Task CannyEdge_OutputIsBinary()
    {
        using var img = CreateGradient(32, 32);
        using var edges = CannyEdge.Detect(img);

        // Every pixel should be either 0 or MaxValue
        bool allBinary = true;
        for (int y = 0; y < (int)edges.Rows; y++)
        {
            var row = edges.GetPixelRow(y);
            for (int x = 0; x < (int)edges.Columns; x++)
            {
                ushort val = row[x * 3];
                if (val != 0 && val != Quantum.MaxValue) { allBinary = false; break; }
            }
            if (!allBinary) break;
        }
        await Assert.That(allBinary).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Hough Line Detection Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task HoughLines_HorizontalLine_DetectsIt()
    {
        // Create image with a single horizontal white line
        using var img = CreateSolid(64, 64, 0, 0, 0);
        DrawHorizontalLine(img, 32, Quantum.MaxValue);

        var lines = HoughLines.Detect(img, threshold: 30);

        await Assert.That(lines.Length).IsGreaterThan(0);
        // Horizontal line should have theta ≈ PI/2 (90°)
        double theta = lines[0].Theta;
        await Assert.That(Math.Abs(theta - Math.PI / 2)).IsLessThan(0.1);
    }

    [Test]
    public async Task HoughLines_VerticalLine_DetectsIt()
    {
        using var img = CreateSolid(64, 64, 0, 0, 0);
        DrawVerticalLine(img, 32, Quantum.MaxValue);

        var lines = HoughLines.Detect(img, threshold: 30);

        await Assert.That(lines.Length).IsGreaterThan(0);
        // Vertical line should have theta ≈ 0
        double theta = lines[0].Theta;
        await Assert.That(theta < 0.1 || theta > Math.PI - 0.1).IsTrue();
    }

    [Test]
    public async Task HoughLines_NoEdges_NoLines()
    {
        using var solid = CreateSolid(32, 32, 0, 0, 0);
        var lines = HoughLines.Detect(solid, threshold: 10);

        await Assert.That(lines.Length).IsEqualTo(0);
    }

    [Test]
    public async Task HoughLines_SortedByVotes()
    {
        using var img = CreateSolid(64, 64, 0, 0, 0);
        DrawHorizontalLine(img, 20, Quantum.MaxValue);
        DrawHorizontalLine(img, 40, Quantum.MaxValue);

        var lines = HoughLines.Detect(img, threshold: 30);
        for (int i = 1; i < lines.Length; i++)
        {
            await Assert.That(lines[i].Votes).IsLessThanOrEqualTo(lines[i - 1].Votes);
        }
    }

    [Test]
    public async Task HoughLines_DrawLines_DoesNotCrash()
    {
        using var img = CreateSolid(64, 64, 128, 128, 128);
        var lines = new[]
        {
            new HoughLines.DetectedLine { Rho = 32, Theta = Math.PI / 2, Votes = 100 }
        };

        HoughLines.DrawLines(img, lines);

        // Just verify it doesn't crash and image is still valid
        await Assert.That((int)img.Columns).IsEqualTo(64);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Connected Components Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConnectedComponents_SingleObject_CountsOne()
    {
        // White square on black background
        using var img = CreateSolid(32, 32, 0, 0, 0);
        FillRect(img, 10, 10, 12, 12, 255);

        var result = ConnectedComponents.Analyze(img, threshold: 0.5);

        await Assert.That(result.ObjectCount).IsEqualTo(1);
        await Assert.That(result.Components[1].Area).IsGreaterThan(0);
    }

    [Test]
    public async Task ConnectedComponents_TwoSeparateObjects()
    {
        using var img = CreateSolid(64, 64, 0, 0, 0);
        FillRect(img, 5, 5, 10, 10, 255);
        FillRect(img, 40, 40, 10, 10, 255);

        var result = ConnectedComponents.Analyze(img, threshold: 0.5);

        await Assert.That(result.ObjectCount).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectedComponents_Connectivity4vs8()
    {
        // Diagonal pixels: 4-connected sees them as separate, 8-connected as one
        using var img = CreateSolid(16, 16, 0, 0, 0);
        SetPixel(img, 5, 5, 255);
        SetPixel(img, 6, 6, 255);
        SetPixel(img, 7, 7, 255);

        var result4 = ConnectedComponents.Analyze(img, threshold: 0.5, connectivity: 4);
        var result8 = ConnectedComponents.Analyze(img, threshold: 0.5, connectivity: 8);

        await Assert.That(result4.ObjectCount).IsGreaterThan(result8.ObjectCount);
        await Assert.That(result8.ObjectCount).IsEqualTo(1);
    }

    [Test]
    public async Task ConnectedComponents_BoundingBox_Correct()
    {
        using var img = CreateSolid(32, 32, 0, 0, 0);
        FillRect(img, 5, 10, 8, 6, 255);

        var result = ConnectedComponents.Analyze(img, threshold: 0.5);

        await Assert.That(result.ObjectCount).IsEqualTo(1);
        var bb = result.Components[1].BoundingBox;
        await Assert.That(bb.X).IsEqualTo(5);
        await Assert.That(bb.Y).IsEqualTo(10);
        await Assert.That(bb.Width).IsEqualTo(8);
        await Assert.That(bb.Height).IsEqualTo(6);
    }

    [Test]
    public async Task ConnectedComponents_Centroid_Reasonable()
    {
        using var img = CreateSolid(32, 32, 0, 0, 0);
        FillRect(img, 10, 10, 10, 10, 255);

        var result = ConnectedComponents.Analyze(img, threshold: 0.5);

        var centroid = result.Components[1].Centroid;
        // Centroid of 10×10 rect at (10,10) should be near (14.5, 14.5)
        await Assert.That(Math.Abs(centroid.X - 14.5)).IsLessThan(1.0);
        await Assert.That(Math.Abs(centroid.Y - 14.5)).IsLessThan(1.0);
    }

    [Test]
    public async Task ConnectedComponents_RenderLabels_CorrectDimensions()
    {
        using var img = CreateSolid(32, 32, 0, 0, 0);
        FillRect(img, 5, 5, 10, 10, 255);

        var result = ConnectedComponents.Analyze(img, threshold: 0.5);
        using var rendered = ConnectedComponents.RenderLabels(result);

        await Assert.That((int)rendered.Columns).IsEqualTo(32);
        await Assert.That((int)rendered.Rows).IsEqualTo(32);
    }

    [Test]
    public async Task ConnectedComponents_AllBlack_NoObjects()
    {
        using var img = CreateSolid(16, 16, 0, 0, 0);
        var result = ConnectedComponents.Analyze(img, threshold: 0.5);

        await Assert.That(result.ObjectCount).IsEqualTo(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Mean-Shift Segmentation Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task MeanShift_SolidImage_Unchanged()
    {
        using var solid = CreateSolid(16, 16, 128, 128, 128);
        using var result = MeanShift.Segment(solid, spatialRadius: 5, colorRadius: 0.1);

        await Assert.That((int)result.Columns).IsEqualTo(16);
        await Assert.That((int)result.Rows).IsEqualTo(16);

        // All pixels should be very close to original
        var srcRow = solid.GetPixelRow(8);
        var dstRow = result.GetPixelRow(8);
        double diff = Math.Abs(srcRow[24] - dstRow[24]) * Quantum.Scale;
        await Assert.That(diff).IsLessThan(0.05);
    }

    [Test]
    public async Task MeanShift_TwoColors_ReducesToTwo()
    {
        // Half black, half white
        using var img = CreateHalfImage(32, 32);
        using var result = MeanShift.Segment(img, spatialRadius: 3, colorRadius: 0.2);

        // After segmentation, center-left should still be dark, center-right still bright
        var row = result.GetPixelRow(16);
        double leftVal = row[5 * 3] * Quantum.Scale;
        double rightVal = row[25 * 3] * Quantum.Scale;

        await Assert.That(leftVal).IsLessThan(0.3);
        await Assert.That(rightVal).IsGreaterThan(0.7);
    }

    [Test]
    public async Task MeanShift_OutputDimensions_MatchInput()
    {
        using var img = CreateGradient(40, 30);
        using var result = MeanShift.Segment(img, spatialRadius: 3, colorRadius: 0.1);

        await Assert.That((int)result.Columns).IsEqualTo(40);
        await Assert.That((int)result.Rows).IsEqualTo(30);
    }

    [Test]
    public async Task MeanShift_SmallRadius_LessSmoothing()
    {
        using var img = CreateCheckerboard(32, 32, 8);
        using var smallR = MeanShift.Segment(img, spatialRadius: 2, colorRadius: 0.05);
        using var largeR = MeanShift.Segment(img, spatialRadius: 8, colorRadius: 0.3);

        // With small radius, more detail preserved (larger variance)
        double varSmall = ComputeVariance(smallR);
        double varLarge = ComputeVariance(largeR);

        await Assert.That(varSmall).IsGreaterThanOrEqualTo(varLarge);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CreateSolid(int w, int h, byte r, byte g, byte b)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte(r);
                row[x * 3 + 1] = Quantum.ScaleFromByte(g);
                row[x * 3 + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return img;
    }

    private static ImageFrame CreateGradient(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(x * 255 / Math.Max(w - 1, 1));
                row[x * 3] = Quantum.ScaleFromByte(v);
                row[x * 3 + 1] = Quantum.ScaleFromByte(v);
                row[x * 3 + 2] = Quantum.ScaleFromByte(v);
            }
        }
        return img;
    }

    private static ImageFrame CreateRandom(int w, int h, int seed)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[x * 3 + 1] = Quantum.ScaleFromByte((byte)rng.Next(256));
                row[x * 3 + 2] = Quantum.ScaleFromByte((byte)rng.Next(256));
            }
        }
        return img;
    }

    private static ImageFrame CreateHalfImage(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)(x < w / 2 ? 0 : 255);
                row[x * 3] = Quantum.ScaleFromByte(v);
                row[x * 3 + 1] = Quantum.ScaleFromByte(v);
                row[x * 3 + 2] = Quantum.ScaleFromByte(v);
            }
        }
        return img;
    }

    private static ImageFrame CreateCheckerboard(int w, int h, int cellSize)
    {
        var img = new ImageFrame();
        img.Initialize((uint)w, (uint)h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                bool isWhite = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                byte v = (byte)(isWhite ? 255 : 0);
                row[x * 3] = Quantum.ScaleFromByte(v);
                row[x * 3 + 1] = Quantum.ScaleFromByte(v);
                row[x * 3 + 2] = Quantum.ScaleFromByte(v);
            }
        }
        return img;
    }

    private static void FillRect(ImageFrame img, int rx, int ry, int rw, int rh, byte val)
    {
        for (int y = ry; y < ry + rh && y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = rx; x < rx + rw && x < (int)img.Columns; x++)
            {
                row[x * 3] = Quantum.ScaleFromByte(val);
                row[x * 3 + 1] = Quantum.ScaleFromByte(val);
                row[x * 3 + 2] = Quantum.ScaleFromByte(val);
            }
        }
    }

    private static void SetPixel(ImageFrame img, int x, int y, byte val)
    {
        var row = img.GetPixelRowForWrite(y);
        row[x * 3] = Quantum.ScaleFromByte(val);
        row[x * 3 + 1] = Quantum.ScaleFromByte(val);
        row[x * 3 + 2] = Quantum.ScaleFromByte(val);
    }

    private static void DrawHorizontalLine(ImageFrame img, int y, ushort val)
    {
        var row = img.GetPixelRowForWrite(y);
        for (int x = 0; x < (int)img.Columns; x++)
        {
            row[x * 3] = val;
            row[x * 3 + 1] = val;
            row[x * 3 + 2] = val;
        }
    }

    private static void DrawVerticalLine(ImageFrame img, int x, ushort val)
    {
        for (int y = 0; y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            row[x * 3] = val;
            row[x * 3 + 1] = val;
            row[x * 3 + 2] = val;
        }
    }

    private static int CountWhitePixels(ImageFrame img)
    {
        int count = 0;
        int channels = img.NumberOfChannels;
        for (int y = 0; y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < (int)img.Columns; x++)
            {
                if (row[x * channels] > Quantum.MaxValue / 2) count++;
            }
        }
        return count;
    }

    private static double ComputeVariance(ImageFrame img)
    {
        int channels = img.NumberOfChannels;
        long count = 0;
        double sum = 0, sumSq = 0;
        for (int y = 0; y < (int)img.Rows; y++)
        {
            var row = img.GetPixelRow(y);
            for (int x = 0; x < (int)img.Columns; x++)
            {
                double val = row[x * channels] * Quantum.Scale;
                sum += val;
                sumSq += val * val;
                count++;
            }
        }
        double mean = sum / count;
        return sumSq / count - mean * mean;
    }
}
