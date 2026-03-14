using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Tests.Core;

public class ImageFrameTests
{
    [Test]
    public async Task Initialize_SetsCorrectDimensions()
    {
        using var image = new ImageFrame();
        image.Initialize(640, 480);

        await Assert.That(image.Columns).IsEqualTo(640L);
        await Assert.That(image.Rows).IsEqualTo(480L);
    }

    [Test]
    public async Task Initialize_RGBImage_Has3Channels()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.SRGB, hasAlpha: false);

        await Assert.That(image.NumberOfChannels).IsEqualTo(3);
        await Assert.That(image.HasAlpha).IsFalse();
    }

    [Test]
    public async Task Initialize_RGBAImage_Has4Channels()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.SRGB, hasAlpha: true);

        await Assert.That(image.NumberOfChannels).IsEqualTo(4);
        await Assert.That(image.HasAlpha).IsTrue();
    }

    [Test]
    public async Task Initialize_GrayImage_Has1Channel()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.Gray, hasAlpha: false);

        await Assert.That(image.NumberOfChannels).IsEqualTo(1);
    }

    [Test]
    public async Task Initialize_GrayAlphaImage_Has2Channels()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.Gray, hasAlpha: true);

        await Assert.That(image.NumberOfChannels).IsEqualTo(2);
    }

    [Test]
    public async Task Initialize_CMYKImage_Has4Channels()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.CMYK, hasAlpha: false);

        await Assert.That(image.NumberOfChannels).IsEqualTo(4);
    }

    [Test]
    public async Task Initialize_CMYKAImage_Has5Channels()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.CMYK, hasAlpha: true);

        await Assert.That(image.NumberOfChannels).IsEqualTo(5);
    }

    [Test]
    public async Task PixelData_InitializedToZero()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10);

        // Read row data into a local array before await
        var row = image.GetPixelRow(0);
        bool allZero = true;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] != 0) { allZero = false; break; }
        }
        await Assert.That(allZero).IsTrue();
    }

    [Test]
    public async Task SetPixel_And_GetPixel_RoundTrips()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10);

        var pixel = PixelInfo.FromRgb(10000, 20000, 30000);
        image.SetPixel(5, 5, pixel);

        var result = image.GetPixel(5, 5);
        await Assert.That(Quantum.Clamp(result.Red)).IsEqualTo((ushort)10000);
        await Assert.That(Quantum.Clamp(result.Green)).IsEqualTo((ushort)20000);
        await Assert.That(Quantum.Clamp(result.Blue)).IsEqualTo((ushort)30000);
    }

    [Test]
    public async Task SetPixelChannel_And_GetPixelChannel_RoundTrips()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10);

        image.SetPixelChannel(3, 7, 0, 50000);
        image.SetPixelChannel(3, 7, 1, 30000);
        image.SetPixelChannel(3, 7, 2, 10000);

        await Assert.That(image.GetPixelChannel(3, 7, 0)).IsEqualTo((ushort)50000);
        await Assert.That(image.GetPixelChannel(3, 7, 1)).IsEqualTo((ushort)30000);
        await Assert.That(image.GetPixelChannel(3, 7, 2)).IsEqualTo((ushort)10000);
    }

    [Test]
    public async Task SetAlpha_EnablesAlphaChannel()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.SRGB, hasAlpha: false);

        // Set some pixel data first
        var pixel = PixelInfo.FromRgb(10000, 20000, 30000);
        image.SetPixel(0, 0, pixel);

        // Enable alpha
        image.SetAlpha(true);

        await Assert.That(image.HasAlpha).IsTrue();
        await Assert.That(image.NumberOfChannels).IsEqualTo(4);

        // Verify RGB data was preserved
        var result = image.GetPixel(0, 0);
        await Assert.That(Quantum.Clamp(result.Red)).IsEqualTo((ushort)10000);
        await Assert.That(Quantum.Clamp(result.Green)).IsEqualTo((ushort)20000);
        await Assert.That(Quantum.Clamp(result.Blue)).IsEqualTo((ushort)30000);

        // Verify alpha was initialized to opaque
        await Assert.That(Quantum.Clamp(result.Alpha)).IsEqualTo(Quantum.Opaque);
    }

    [Test]
    public async Task SetPixel_WithAlpha_StoresAlphaCorrectly()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10, ColorspaceType.SRGB, hasAlpha: true);

        var pixel = PixelInfo.FromRgba(10000, 20000, 30000, 40000);
        image.SetPixel(0, 0, pixel);

        var result = image.GetPixel(0, 0);
        await Assert.That(Quantum.Clamp(result.Alpha)).IsEqualTo((ushort)40000);
    }

    [Test]
    public async Task TotalPixels_ReturnsCorrectCount()
    {
        using var image = new ImageFrame();
        image.Initialize(100, 200);

        await Assert.That(image.TotalPixels).IsEqualTo(20000L);
    }

    [Test]
    public async Task GetPixelRow_AllRows_Accessible()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 5);

        // Should not throw for any valid row, and each should have correct length
        bool allCorrectLength = true;
        for (long y = 0; y < 5; y++)
        {
            var row = image.GetPixelRow(y);
            if (row.Length != 30) { allCorrectLength = false; break; }  // 10 pixels * 3 channels
        }
        await Assert.That(allCorrectLength).IsTrue();
    }

    [Test]
    public void Initialize_ZeroDimensions_Throws()
    {
        using var image = new ImageFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Initialize(0, 10));
    }

    [Test]
    public void Initialize_NegativeDimensions_Throws()
    {
        using var image = new ImageFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Initialize(-1, 10));
    }

    [Test]
    public async Task DefaultProperties_AreCorrect()
    {
        using var image = new ImageFrame();
        image.Initialize(10, 10);

        await Assert.That(image.Colorspace).IsEqualTo(ColorspaceType.SRGB);
        await Assert.That(image.Depth).IsEqualTo(16);
        await Assert.That(image.StorageClass).IsEqualTo(StorageClass.Direct);
        await Assert.That(image.Quality).IsEqualTo(75);
        await Assert.That(image.ResolutionX).IsEqualTo(72.0);
        await Assert.That(image.ResolutionY).IsEqualTo(72.0);
    }
}
