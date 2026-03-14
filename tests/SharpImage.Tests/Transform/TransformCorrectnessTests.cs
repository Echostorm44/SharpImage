using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Transform;

namespace SharpImage.Tests.Transform;

/// <summary>
/// Mathematical correctness verification for transform operations.
/// Tests pixel-level accuracy, not just "doesn't crash" or dimension checks.
/// </summary>
public class TransformCorrectnessTests
{
    /// <summary>Creates a test frame with deterministic pixel values.</summary>
    private static ImageFrame CreatePatternFrame(int width, int height, bool hasAlpha = false)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte((byte)((x * 7 + y * 13) % 256));
                if (channels > 1) row[offset + 1] = Quantum.ScaleFromByte((byte)((x * 11 + y * 3) % 256));
                if (channels > 2) row[offset + 2] = Quantum.ScaleFromByte((byte)((x * 5 + y * 17) % 256));
                if (hasAlpha) row[offset + channels - 1] = Quantum.MaxValue;
            }
        }

        return frame;
    }

    /// <summary>Creates a solid color frame for simple verification.</summary>
    private static ImageFrame CreateSolidFrame(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;

        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                row[offset] = r;
                row[offset + 1] = g;
                row[offset + 2] = b;
            }
        }

        return frame;
    }

    // ─── Flip Correctness ────────────────────────────────────────

    [Test]
    public async Task Flip_SwapsRows_PixelPerfect()
    {
        using var source = CreatePatternFrame(8, 6);
        using var flipped = Geometry.Flip(source);

        await Assert.That((int)flipped.Columns).IsEqualTo(8);
        await Assert.That((int)flipped.Rows).IsEqualTo(6);

        int channels = source.NumberOfChannels;
        for (int y = 0; y < 6; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var flipRow = flipped.GetPixelRow(5 - y);
            for (int x = 0; x < 8 * channels; x++)
            {
                if (srcRow[x] != flipRow[x])
                    throw new Exception($"Flip mismatch at ({x / channels},{y}) ch{x % channels}: " +
                        $"expected {srcRow[x]}, got {flipRow[x]}");
            }
        }
    }

    [Test]
    public async Task Flip_DoubleFlip_RestoresOriginal()
    {
        using var source = CreatePatternFrame(10, 8);
        using var once = Geometry.Flip(source);
        using var twice = Geometry.Flip(once);

        AssertFramesIdentical(source, twice);
        await Assert.That(true).IsTrue();
    }

    // ─── Flop Correctness ────────────────────────────────────────

    [Test]
    public async Task Flop_SwapsCols_PixelPerfect()
    {
        using var source = CreatePatternFrame(8, 6);
        using var flopped = Geometry.Flop(source);

        int channels = source.NumberOfChannels;
        for (int y = 0; y < 6; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var flopRow = flopped.GetPixelRow(y);
            for (int x = 0; x < 8; x++)
            {
                int mirrorX = 7 - x;
                for (int c = 0; c < channels; c++)
                {
                    if (srcRow[x * channels + c] != flopRow[mirrorX * channels + c])
                        throw new Exception($"Flop mismatch at ({x},{y}) ch{c}");
                }
            }
        }

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Flop_DoubleFlop_RestoresOriginal()
    {
        using var source = CreatePatternFrame(10, 8);
        using var once = Geometry.Flop(source);
        using var twice = Geometry.Flop(once);

        AssertFramesIdentical(source, twice);
        await Assert.That(true).IsTrue();
    }

    // ─── Rotate Correctness ──────────────────────────────────────

    [Test]
    public async Task Rotate90_CorrectDimensions()
    {
        using var source = CreatePatternFrame(12, 8);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate90);

        await Assert.That((int)rotated.Columns).IsEqualTo(8);
        await Assert.That((int)rotated.Rows).IsEqualTo(12);
    }

    [Test]
    public async Task Rotate180_PixelPerfect()
    {
        using var source = CreatePatternFrame(8, 6);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate180);

        int channels = source.NumberOfChannels;
        int w = 8, h = 6;
        for (int y = 0; y < h; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var rotRow = rotated.GetPixelRow(h - 1 - y);
            for (int x = 0; x < w; x++)
            {
                int mx = w - 1 - x;
                for (int c = 0; c < channels; c++)
                {
                    if (srcRow[x * channels + c] != rotRow[mx * channels + c])
                        throw new Exception($"Rotate180 mismatch at ({x},{y}) ch{c}");
                }
            }
        }

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Rotate360_RestoresOriginal()
    {
        using var source = CreatePatternFrame(10, 8);
        using var r90 = Geometry.Rotate(source, RotationAngle.Rotate90);
        using var r180 = Geometry.Rotate(r90, RotationAngle.Rotate90);
        using var r270 = Geometry.Rotate(r180, RotationAngle.Rotate90);
        using var r360 = Geometry.Rotate(r270, RotationAngle.Rotate90);

        AssertFramesIdentical(source, r360);
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Rotate90_TopLeft_GoesToTopRight()
    {
        using var source = CreatePatternFrame(8, 6);
        using var rotated = Geometry.Rotate(source, RotationAngle.Rotate90);

        int channels = source.NumberOfChannels;
        var srcRow0 = source.GetPixelRow(0);
        var rotRow0 = rotated.GetPixelRow(0);
        int lastCol = (int)rotated.Columns - 1;

        // Source pixel (0,0) should appear at (rotated.Columns-1, 0) after 90° CW rotation
        for (int c = 0; c < channels; c++)
        {
            if (srcRow0[c] != rotRow0[lastCol * channels + c])
                throw new Exception($"Rotate90 corner mismatch ch{c}: " +
                    $"src(0,0)={srcRow0[c]}, rot({lastCol},0)={rotRow0[lastCol * channels + c]}");
        }

        await Assert.That(true).IsTrue();
    }

    // ─── Crop Correctness ────────────────────────────────────────

    [Test]
    public async Task Crop_ExtractsCorrectRegion()
    {
        using var source = CreatePatternFrame(16, 12);
        using var cropped = Geometry.Crop(source, 4, 3, 8, 6);

        await Assert.That((int)cropped.Columns).IsEqualTo(8);
        await Assert.That((int)cropped.Rows).IsEqualTo(6);

        int channels = source.NumberOfChannels;
        for (int y = 0; y < 6; y++)
        {
            var srcRow = source.GetPixelRow(y + 3);
            var cropRow = cropped.GetPixelRow(y);
            for (int x = 0; x < 8; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    ushort expected = srcRow[(x + 4) * channels + c];
                    ushort actual = cropRow[x * channels + c];
                    if (expected != actual)
                        throw new Exception($"Crop mismatch at ({x},{y}) ch{c}: " +
                            $"expected {expected}, got {actual}");
                }
            }
        }
    }

    [Test]
    public async Task Crop_FullImage_PreservesAllPixels()
    {
        using var source = CreatePatternFrame(10, 8);
        using var cropped = Geometry.Crop(source, 0, 0, 10, 8);

        AssertFramesIdentical(source, cropped);
        await Assert.That(true).IsTrue();
    }

    // ─── Resize Correctness ──────────────────────────────────────

    [Test]
    public async Task Resize_NearestNeighbor_2x_DoublesPixels()
    {
        using var source = CreatePatternFrame(4, 4);
        using var resized = Resize.Apply(source, 8, 8, InterpolationMethod.NearestNeighbor);

        await Assert.That((int)resized.Columns).IsEqualTo(8);
        await Assert.That((int)resized.Rows).IsEqualTo(8);

        // Each 2x2 block should match the source pixel
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 4; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var resRow0 = resized.GetPixelRow(y * 2);
            var resRow1 = resized.GetPixelRow(y * 2 + 1);
            for (int x = 0; x < 4; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    ushort expected = srcRow[x * channels + c];
                    ushort a00 = resRow0[(x * 2) * channels + c];
                    ushort a10 = resRow0[(x * 2 + 1) * channels + c];
                    ushort a01 = resRow1[(x * 2) * channels + c];
                    ushort a11 = resRow1[(x * 2 + 1) * channels + c];

                    if (a00 != expected || a10 != expected || a01 != expected || a11 != expected)
                        throw new Exception($"NN 2x mismatch at ({x},{y}): " +
                            $"expected {expected}, got [{a00},{a10},{a01},{a11}]");
                }
            }
        }
    }

    [Test]
    public async Task Resize_SolidColor_PreservesColor_AllMethods()
    {
        ushort r = 32768, g = 16384, b = 49152;
        using var source = CreateSolidFrame(16, 16, r, g, b);

        // Every resize method should preserve a solid color perfectly
        InterpolationMethod[] methods =
        [
            InterpolationMethod.NearestNeighbor,
            InterpolationMethod.Bilinear,
            InterpolationMethod.Bicubic,
            InterpolationMethod.Lanczos3,
            InterpolationMethod.Mitchell,
            InterpolationMethod.Catrom,
            InterpolationMethod.Gaussian,
            InterpolationMethod.Hermite,
            InterpolationMethod.Spline,
            InterpolationMethod.Triangle,
        ];

        foreach (var method in methods)
        {
            using var resized = Resize.Apply(source, 8, 8, method);
            int channels = resized.NumberOfChannels;

            // Check all pixels and collect worst error info
            int worstDiff = 0;
            int worstX = 0, worstY = 0, worstCh = 0;
            ushort worstExpected = 0, worstActual = 0;

            for (int y = 0; y < 8; y++)
            {
                var row = resized.GetPixelRow(y);
                for (int x = 0; x < 8; x++)
                {
                    ushort[] expected = [r, g, b];
                    for (int c = 0; c < 3; c++)
                    {
                        int diff = Math.Abs(row[x * channels + c] - expected[c]);
                        if (diff > worstDiff)
                        {
                            worstDiff = diff;
                            worstX = x; worstY = y; worstCh = c;
                            worstExpected = expected[c];
                            worstActual = row[x * channels + c];
                        }
                    }
                }
            }

            // Allow up to 2 quantum tolerance for rounding in filter math
            if (worstDiff > 2)
                throw new Exception($"{method} solid color mismatch at ({worstX},{worstY}) ch{worstCh}: " +
                    $"expected {worstExpected}, got {worstActual} diff={worstDiff}");
        }

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Resize_SameSize_NearestNeighbor_PreservesPixels()
    {
        using var source = CreatePatternFrame(16, 12);
        using var resized = Resize.Apply(source, 16, 12, InterpolationMethod.NearestNeighbor);

        AssertFramesIdentical(source, resized);
        await Assert.That(true).IsTrue();
    }

    [Test]
    [Arguments(InterpolationMethod.NearestNeighbor)]
    [Arguments(InterpolationMethod.Bilinear)]
    [Arguments(InterpolationMethod.Bicubic)]
    [Arguments(InterpolationMethod.Lanczos3)]
    [Arguments(InterpolationMethod.Mitchell)]
    [Arguments(InterpolationMethod.Catrom)]
    [Arguments(InterpolationMethod.Gaussian)]
    [Arguments(InterpolationMethod.Hermite)]
    [Arguments(InterpolationMethod.Spline)]
    [Arguments(InterpolationMethod.Triangle)]
    [Arguments(InterpolationMethod.Sinc)]
    [Arguments(InterpolationMethod.Hann)]
    [Arguments(InterpolationMethod.Hamming)]
    [Arguments(InterpolationMethod.Blackman)]
    [Arguments(InterpolationMethod.Kaiser)]
    [Arguments(InterpolationMethod.Lanczos2)]
    [Arguments(InterpolationMethod.Lanczos4)]
    [Arguments(InterpolationMethod.Lanczos5)]
    [Arguments(InterpolationMethod.Parzen)]
    [Arguments(InterpolationMethod.Bohman)]
    [Arguments(InterpolationMethod.Bartlett)]
    [Arguments(InterpolationMethod.Welch)]
    [Arguments(InterpolationMethod.Lagrange)]
    public async Task Resize_AllMethods_ProduceCorrectDimensions(InterpolationMethod method)
    {
        using var source = CreatePatternFrame(32, 24);
        using var resized = Resize.Apply(source, 16, 12, method);

        await Assert.That((int)resized.Columns).IsEqualTo(16);
        await Assert.That((int)resized.Rows).IsEqualTo(12);
    }

    // ─── Transpose / Transverse ──────────────────────────────────

    [Test]
    public async Task Transpose_SwapsDimensions()
    {
        using var source = CreatePatternFrame(12, 8);
        using var transposed = Geometry.Transpose(source);

        await Assert.That((int)transposed.Columns).IsEqualTo(8);
        await Assert.That((int)transposed.Rows).IsEqualTo(12);

        // Transpose: pixel (x,y) → (y,x)
        int channels = source.NumberOfChannels;
        for (int y = 0; y < 8; y++)
        {
            var srcRow = source.GetPixelRow(y);
            for (int x = 0; x < 12; x++)
            {
                var transRow = transposed.GetPixelRow(x);
                for (int c = 0; c < channels; c++)
                {
                    if (srcRow[x * channels + c] != transRow[y * channels + c])
                        throw new Exception($"Transpose mismatch at src({x},{y})->transposed({y},{x}) ch{c}");
                }
            }
        }

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Transpose_DoubleTranspose_RestoresOriginal()
    {
        using var source = CreatePatternFrame(12, 8);
        using var once = Geometry.Transpose(source);
        using var twice = Geometry.Transpose(once);

        AssertFramesIdentical(source, twice);
        await Assert.That(true).IsTrue();
    }

    // ─── Roll ────────────────────────────────────────────────────

    [Test]
    public async Task Roll_Horizontal_ShiftsPixelsCorrectly()
    {
        using var source = CreatePatternFrame(8, 4);
        using var rolled = Geometry.Roll(source, 3, 0);

        int channels = source.NumberOfChannels;
        for (int y = 0; y < 4; y++)
        {
            var srcRow = source.GetPixelRow(y);
            var rollRow = rolled.GetPixelRow(y);
            for (int x = 0; x < 8; x++)
            {
                int newX = (x + 3) % 8;
                for (int c = 0; c < channels; c++)
                {
                    if (srcRow[x * channels + c] != rollRow[newX * channels + c])
                        throw new Exception($"Roll mismatch at ({x},{y})->({newX},{y}) ch{c}");
                }
            }
        }

        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task Roll_FullWidth_RestoresOriginal()
    {
        using var source = CreatePatternFrame(8, 4);
        using var rolled = Geometry.Roll(source, 8, 0);

        AssertFramesIdentical(source, rolled);
        await Assert.That(true).IsTrue();
    }

    // ─── Sample (nearest neighbor resize) ────────────────────────

    [Test]
    public async Task Sample_HalfSize_CorrectDimensions()
    {
        using var source = CreatePatternFrame(16, 12);
        using var sampled = Resize.Sample(source, 8, 6);

        await Assert.That((int)sampled.Columns).IsEqualTo(8);
        await Assert.That((int)sampled.Rows).IsEqualTo(6);
    }

    // ─── Composite Correctness ───────────────────────────────────

    [Test]
    public async Task Composite_Over_OpaqueOverlay_ReplacesBase()
    {
        using var baseFrame = CreateSolidFrame(8, 8, 0, 0, 0);
        baseFrame.Dispose();
        var baseWithAlpha = new ImageFrame();
        baseWithAlpha.Initialize(8, 8, ColorspaceType.SRGB, true);
        for (int y = 0; y < 8; y++)
        {
            var row = baseWithAlpha.GetPixelRowForWrite(y);
            for (int x = 0; x < 8; x++)
            {
                row[x * 4] = 0;
                row[x * 4 + 1] = 0;
                row[x * 4 + 2] = 0;
                row[x * 4 + 3] = Quantum.MaxValue;
            }
        }

        var overlay = new ImageFrame();
        overlay.Initialize(4, 4, ColorspaceType.SRGB, true);
        for (int y = 0; y < 4; y++)
        {
            var row = overlay.GetPixelRowForWrite(y);
            for (int x = 0; x < 4; x++)
            {
                row[x * 4] = Quantum.MaxValue;
                row[x * 4 + 1] = 0;
                row[x * 4 + 2] = 0;
                row[x * 4 + 3] = Quantum.MaxValue;
            }
        }

        Composite.Apply(baseWithAlpha, overlay, 2, 2);

        // Check that overlay region is red
        var resultRow = baseWithAlpha.GetPixelRow(3);
        ushort overlayR = resultRow[3 * 4]; // pixel at (3, 3) should be red
        await Assert.That(overlayR).IsEqualTo(Quantum.MaxValue);

        // Check that non-overlay region is black
        var topRow = baseWithAlpha.GetPixelRow(0);
        ushort cornerR = topRow[0]; // pixel at (0, 0) should be black
        await Assert.That(cornerR).IsEqualTo((ushort)0);

        baseWithAlpha.Dispose();
        overlay.Dispose();
    }

    // ─── Helper ──────────────────────────────────────────────────

    private static void AssertFramesIdentical(ImageFrame expected, ImageFrame actual)
    {
        if (expected.Columns != actual.Columns || expected.Rows != actual.Rows)
            throw new Exception($"Dimension mismatch: {expected.Columns}x{expected.Rows} vs {actual.Columns}x{actual.Rows}");

        int channels = expected.NumberOfChannels;
        for (int y = 0; y < (int)expected.Rows; y++)
        {
            var srcRow = expected.GetPixelRow(y);
            var dstRow = actual.GetPixelRow(y);
            for (int x = 0; x < (int)expected.Columns * channels; x++)
            {
                if (srcRow[x] != dstRow[x])
                    throw new Exception($"Pixel mismatch at linear index {x} row {y}: " +
                        $"expected {srcRow[x]}, got {dstRow[x]}");
            }
        }
    }
}
