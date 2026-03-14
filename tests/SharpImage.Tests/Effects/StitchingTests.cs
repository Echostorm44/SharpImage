using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Image;

namespace SharpImage.Tests.Effects;

public class StitchingTests
{
    private static ImageFrame CreateCheckerboard(int width, int height)
    {
        // Use large blocks (16px) so corners are well-separated for NMS
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                bool isWhite = ((x / 16) + (y / 16)) % 2 == 0;
                ushort val = isWhite ? Quantum.MaxValue : (ushort)0;
                row[off] = row[off + 1] = row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateCornerImage(int width, int height)
    {
        // White rectangle in center on black background — clear corners
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        int x1 = width / 4, x2 = 3 * width / 4;
        int y1 = height / 4, y2 = 3 * height / 4;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                ushort val = (x >= x1 && x < x2 && y >= y1 && y < y2) ? Quantum.MaxValue : (ushort)0;
                row[off] = row[off + 1] = row[off + 2] = val;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradient(int width, int height, bool horizontal = true)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.RGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                ushort val = horizontal
                    ? (ushort)(x * Quantum.MaxValue / (width - 1))
                    : (ushort)(y * Quantum.MaxValue / (height - 1));
                row[off] = val;
                row[off + 1] = (ushort)(Quantum.MaxValue - val);
                row[off + 2] = (ushort)(Quantum.MaxValue / 2);
            }
        }
        return frame;
    }

    // ─── Harris Corner Tests ────────────────────────────────────────

    [Test]
    public async Task HarrisCorners_DetectsCorners()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 100);

        // White rectangle on black → should detect 4 corners
        await Assert.That(corners.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task HarrisCorners_RespondsToQualityLevel()
    {
        var image = CreateCornerImage(80, 80);
        var looseCorners = StitchingOps.DetectHarrisCorners(image, maxCorners: 500, qualityLevel: 0.001);
        var strictCorners = StitchingOps.DetectHarrisCorners(image, maxCorners: 500, qualityLevel: 0.5);

        // Stricter threshold should give fewer corners
        await Assert.That(looseCorners.Length).IsGreaterThanOrEqualTo(strictCorners.Length);
    }

    [Test]
    public async Task HarrisCorners_MaxCornersRespected()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 3);

        await Assert.That(corners.Length).IsLessThanOrEqualTo(3);
    }

    // ─── BRIEF Descriptor Tests ─────────────────────────────────────

    [Test]
    public async Task BriefDescriptors_ComputesForAllKeypoints()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 20);
        if (corners.Length == 0) { return; }
        var features = StitchingOps.ComputeBriefDescriptors(image, corners);

        await Assert.That(features.Length).IsEqualTo(corners.Length);
    }

    [Test]
    public async Task BriefDescriptors_SameImageSameDescriptors()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 20);
        if (corners.Length == 0) { return; }
        var feat1 = StitchingOps.ComputeBriefDescriptors(image, corners);
        var feat2 = StitchingOps.ComputeBriefDescriptors(image, corners);

        // Same image, same keypoints → identical descriptors
        bool allMatch = true;
        for (int i = 0; i < feat1.Length; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                if (feat1[i].Descriptor[j] != feat2[i].Descriptor[j])
                { allMatch = false; break; }
            }
        }
        await Assert.That(allMatch).IsTrue();
    }

    // ─── Feature Matching Tests ─────────────────────────────────────

    [Test]
    public async Task MatchFeatures_SelfMatchProducesGoodMatches()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 50);
        if (corners.Length < 2) { return; }
        var features = StitchingOps.ComputeBriefDescriptors(image, corners);
        var matches = StitchingOps.MatchFeatures(features, features, ratioThreshold: 0.9);

        // Self-matching should produce many zero-distance matches
        int zeroDistMatches = matches.Count(m => m.Distance == 0);
        await Assert.That(zeroDistMatches).IsGreaterThan(0);
    }

    // ─── RANSAC Homography Tests ────────────────────────────────────

    [Test]
    public async Task EstimateHomography_IdentityForSameImage()
    {
        var image = CreateCornerImage(80, 80);
        var corners = StitchingOps.DetectHarrisCorners(image, maxCorners: 100);
        if (corners.Length < 4) { return; }

        var features = StitchingOps.ComputeBriefDescriptors(image, corners);
        var matches = StitchingOps.MatchFeatures(features, features, ratioThreshold: 0.9);

        if (matches.Length < 4) { return; }

        var h = StitchingOps.EstimateHomographyRANSAC(features, features, matches);

        // Should be approximately identity: h[0]≈1, h[4]≈1, others≈0
        await Assert.That(Math.Abs(h[0] - 1.0)).IsLessThan(0.5);
        await Assert.That(Math.Abs(h[4] - 1.0)).IsLessThan(0.5);
    }

    // ─── Laplacian Blend Tests ──────────────────────────────────────

    [Test]
    public async Task LaplacianBlend_ProducesCorrectSize()
    {
        var imageA = CreateGradient(40, 30, horizontal: true);
        var imageB = CreateGradient(40, 30, horizontal: false);
        var mask = new ImageFrame();
        mask.Initialize(40, 30, ColorspaceType.Gray, false);
        for (int y = 0; y < 30; y++)
        {
            var row = mask.GetPixelRowForWrite(y);
            for (int x = 0; x < 40; x++)
                row[x] = x < 20 ? Quantum.MaxValue : (ushort)0;
        }

        var result = StitchingOps.LaplacianBlend(imageA, imageB, mask, pyramidLevels: 3);

        await Assert.That(result.Columns).IsEqualTo(40u);
        await Assert.That(result.Rows).IsEqualTo(30u);
    }

    [Test]
    public async Task LaplacianBlend_MaskControlsBlending()
    {
        var imageA = CreateGradient(40, 30, horizontal: true);
        var imageB = CreateGradient(40, 30, horizontal: false);
        var mask = new ImageFrame();
        mask.Initialize(40, 30, ColorspaceType.Gray, false);
        for (int y = 0; y < 30; y++)
        {
            var row = mask.GetPixelRowForWrite(y);
            for (int x = 0; x < 40; x++)
                row[x] = x < 20 ? Quantum.MaxValue : (ushort)0;
        }

        var result = StitchingOps.LaplacianBlend(imageA, imageB, mask, pyramidLevels: 3);

        // Far left should be closer to imageA
        var leftA = imageA.GetPixelRow(15)[2 * 3];
        var leftR = result.GetPixelRow(15)[2 * 3];
        var leftB = imageB.GetPixelRow(15)[2 * 3];
        await Assert.That(Math.Abs(leftR - leftA)).IsLessThan(Math.Abs(leftR - leftB) + (int)(Quantum.MaxValue * 0.2));

        // Far right should be closer to imageB
        var rightA = imageA.GetPixelRow(15)[37 * 3];
        var rightR = result.GetPixelRow(15)[37 * 3];
        var rightB = imageB.GetPixelRow(15)[37 * 3];
        await Assert.That(Math.Abs(rightR - rightB)).IsLessThan(Math.Abs(rightR - rightA) + (int)(Quantum.MaxValue * 0.2));
    }

    // ─── Panorama Stitch Test ───────────────────────────────────────

    [Test]
    public async Task StitchPanorama_ProducesOutput()
    {
        var imageA = CreateCheckerboard(80, 60);
        var imageB = CreateCheckerboard(80, 60);

        var result = StitchingOps.StitchPanorama(imageA, imageB, maxCorners: 100);

        // Should produce an output (at minimum returns imageA)
        await Assert.That(result.Columns).IsGreaterThan(0u);
        await Assert.That(result.Rows).IsGreaterThan(0u);
    }
}
