// Tests for Channel operations: separate, combine, swap.

using SharpImage.Channel;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Tests.Channel;

public class ChannelOpsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame CreateTestImage(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height);

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                // Red gradient left-right, green gradient top-bottom, blue constant
                row[off] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(1, width - 1)));
                row[off + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(1, height - 1)));
                row[off + 2] = Quantum.ScaleFromByte(128);
            }
        }

        return img;
    }

    private static ImageFrame CreateTestImageWithAlpha(int width, int height)
    {
        var img = new ImageFrame();
        img.Initialize(width, height, hasAlpha: true);

        for (int y = 0; y < height; y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 4;
                row[off] = Quantum.ScaleFromByte((byte)(x * 255 / Math.Max(1, width - 1)));
                row[off + 1] = Quantum.ScaleFromByte((byte)(y * 255 / Math.Max(1, height - 1)));
                row[off + 2] = Quantum.ScaleFromByte(128);
                row[off + 3] = Quantum.ScaleFromByte((byte)((x + y) * 255 / Math.Max(1, width + height - 2)));
            }
        }

        return img;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Separate Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Separate_RgbImage_ReturnsThreeChannels()
    {
        using var img = CreateTestImage(32, 32);
        var channels = ChannelOps.Separate(img);

        await Assert.That(channels.Length).IsEqualTo(3);
        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Separate_RgbaImage_ReturnsFourChannels()
    {
        using var img = CreateTestImageWithAlpha(32, 32);
        var channels = ChannelOps.Separate(img);

        await Assert.That(channels.Length).IsEqualTo(4);
        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Separate_RedChannel_ContainsHorizontalGradient()
    {
        using var img = CreateTestImage(64, 32);
        var channels = ChannelOps.Separate(img);
        var redChannel = channels[0];

        // Red gradient goes left to right, so left side should be dark, right side bright
        ushort topLeft = redChannel.GetPixelRow(0)[0]; // R of pixel (0,0)
        ushort topRight = redChannel.GetPixelRow(0)[(int)((redChannel.Columns - 1) * 3)];

        await Assert.That(topRight).IsGreaterThan(topLeft);

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Separate_GreenChannel_ContainsVerticalGradient()
    {
        using var img = CreateTestImage(32, 64);
        var channels = ChannelOps.Separate(img);
        var greenChannel = channels[1];

        // Green gradient goes top to bottom
        ushort topLeft = greenChannel.GetPixelRow(0)[0];
        ushort bottomLeft = greenChannel.GetPixelRow(greenChannel.Rows - 1)[0];

        await Assert.That(bottomLeft).IsGreaterThan(topLeft);

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Separate_BlueChannel_IsConstant()
    {
        using var img = CreateTestImage(16, 16);
        var channels = ChannelOps.Separate(img);
        var blueChannel = channels[2];

        ushort expected = Quantum.ScaleFromByte(128);
        ushort val = blueChannel.GetPixelRow(0)[0];

        await Assert.That(val).IsEqualTo(expected);

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Separate_OutputsAreGrayscale()
    {
        using var img = CreateTestImage(16, 16);
        var channels = ChannelOps.Separate(img);

        // Each channel image should have R=G=B (grayscale)
        bool allGray = true;
        foreach (var ch in channels)
        {
            var row = ch.GetPixelRow(8);
            ushort r = row[24]; // pixel 8, R
            ushort g = row[25]; // pixel 8, G
            ushort b = row[26]; // pixel 8, B
            if (r != g || g != b) { allGray = false; break; }
            ch.Dispose();
        }
        if (allGray) foreach (var ch in channels) { try { ch.Dispose(); } catch { } }

        await Assert.That(allGray).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SeparateChannel Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task SeparateChannel_RedOnly_MatchesSeparateResult()
    {
        using var img = CreateTestImage(32, 32);
        using var redOnly = ChannelOps.SeparateChannel(img, 0);
        var allChannels = ChannelOps.Separate(img);

        ushort fromSingle = redOnly.GetPixelRow(16)[48]; // pixel 16
        ushort fromAll = allChannels[0].GetPixelRow(16)[48];

        await Assert.That(fromSingle).IsEqualTo(fromAll);

        foreach (var ch in allChannels) ch.Dispose();
    }

    [Test]
    public async Task SeparateChannel_InvalidIndex_Throws()
    {
        using var img = CreateTestImage(8, 8);

        await Assert.That(() => ChannelOps.SeparateChannel(img, 5))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SeparateChannel_AlphaFromRgba_ReturnsAlphaValues()
    {
        using var img = CreateTestImageWithAlpha(32, 32);
        using var alphaChannel = ChannelOps.SeparateChannel(img, 3);

        // Alpha gradient goes diagonal (x+y based)
        ushort topLeft = alphaChannel.GetPixelRow(0)[0];
        ushort bottomRight = alphaChannel.GetPixelRow(31)[(int)(31 * 3)];

        await Assert.That(bottomRight).IsGreaterThan(topLeft);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Combine Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Combine_ThreeChannels_ProducesRgbImage()
    {
        using var img = CreateTestImage(32, 32);
        var channels = ChannelOps.Separate(img);

        using var combined = ChannelOps.Combine(channels);

        await Assert.That(combined.NumberOfChannels).IsEqualTo(3);
        await Assert.That(combined.HasAlpha).IsFalse();

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Combine_FourChannels_ProducesRgbaImage()
    {
        using var img = CreateTestImageWithAlpha(32, 32);
        var channels = ChannelOps.Separate(img);

        using var combined = ChannelOps.Combine(channels);

        await Assert.That(combined.NumberOfChannels).IsEqualTo(4);
        await Assert.That(combined.HasAlpha).IsTrue();

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Combine_RoundTrip_PreservesPixelData()
    {
        using var original = CreateTestImage(32, 32);
        var channels = ChannelOps.Separate(original);
        using var combined = ChannelOps.Combine(channels);

        // Check row 16 matches
        var origRow = original.GetPixelRow(16);
        var combRow = combined.GetPixelRow(16);

        bool allMatch = true;
        for (int x = 0; x < 32; x++)
        {
            int off = x * 3;
            if (combRow[off] != origRow[off] || combRow[off + 1] != origRow[off + 1] || combRow[off + 2] != origRow[off + 2])
            {
                allMatch = false;
                break;
            }
        }
        await Assert.That(allMatch).IsTrue();

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Combine_RoundTripRgba_PreservesAlpha()
    {
        using var original = CreateTestImageWithAlpha(32, 32);
        var channels = ChannelOps.Separate(original);
        using var combined = ChannelOps.Combine(channels);

        var origRow = original.GetPixelRow(20);
        var combRow = combined.GetPixelRow(20);

        bool allMatch = true;
        for (int x = 0; x < 32; x++)
        {
            int off = x * 4;
            if (combRow[off] != origRow[off] || combRow[off + 1] != origRow[off + 1] ||
                combRow[off + 2] != origRow[off + 2] || combRow[off + 3] != origRow[off + 3])
            {
                allMatch = false;
                break;
            }
        }
        await Assert.That(allMatch).IsTrue();

        foreach (var ch in channels) ch.Dispose();
    }

    [Test]
    public async Task Combine_MismatchedSizes_Throws()
    {
        using var small = CreateTestImage(16, 16);
        using var big = CreateTestImage(32, 32);

        var channels = new[] { small, big, small };

        await Assert.That(() => ChannelOps.Combine(channels))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Combine_WrongCount_Throws()
    {
        using var ch1 = CreateTestImage(8, 8);
        using var ch2 = CreateTestImage(8, 8);

        await Assert.That(() => ChannelOps.Combine([ch1, ch2]))
            .Throws<ArgumentException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SwapChannels Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task SwapChannels_RedAndBlue_SwapsValues()
    {
        using var img = CreateTestImage(16, 16);

        ushort origR = img.GetPixelRow(8)[24]; // pixel(8,8) R
        ushort origB = img.GetPixelRow(8)[26]; // pixel(8,8) B

        ChannelOps.SwapChannels(img, 0, 2); // swap R and B

        ushort newR = img.GetPixelRow(8)[24];
        ushort newB = img.GetPixelRow(8)[26];

        await Assert.That(newR).IsEqualTo(origB);
        await Assert.That(newB).IsEqualTo(origR);
    }

    [Test]
    public async Task SwapChannels_SameChannel_NoOp()
    {
        using var img = CreateTestImage(8, 8);
        ushort before = img.GetPixelRow(4)[12];

        ChannelOps.SwapChannels(img, 1, 1);

        ushort after = img.GetPixelRow(4)[12];
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task SwapChannels_InvalidIndex_Throws()
    {
        using var img = CreateTestImage(8, 8);

        await Assert.That(() => ChannelOps.SwapChannels(img, 0, 5))
            .Throws<ArgumentOutOfRangeException>();
    }

    // ═══════════════════════════════════════════════════════════════════
    // GetChannelName Tests
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetChannelName_ReturnsCorrectNames()
    {
        await Assert.That(ChannelOps.GetChannelName(0, false)).IsEqualTo("Red");
        await Assert.That(ChannelOps.GetChannelName(1, false)).IsEqualTo("Green");
        await Assert.That(ChannelOps.GetChannelName(2, false)).IsEqualTo("Blue");
        await Assert.That(ChannelOps.GetChannelName(3, true)).IsEqualTo("Alpha");
    }
}
