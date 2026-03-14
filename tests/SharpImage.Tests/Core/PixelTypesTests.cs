using SharpImage.Core;

namespace SharpImage.Tests.Core;

public class PixelTypesTests
{
    [Test]
    public async Task PixelInfo_FromRgb_SetsCorrectValues()
    {
        var pixel = PixelInfo.FromRgb(10000, 20000, 30000);

        await Assert.That(pixel.Red).IsEqualTo(10000.0);
        await Assert.That(pixel.Green).IsEqualTo(20000.0);
        await Assert.That(pixel.Blue).IsEqualTo(30000.0);
        await Assert.That(pixel.Alpha).IsEqualTo((double)Quantum.Opaque);
        await Assert.That(pixel.Colorspace).IsEqualTo(ColorspaceType.SRGB);
        await Assert.That(pixel.StorageClass).IsEqualTo(StorageClass.Direct);
    }

    [Test]
    public async Task PixelInfo_FromRgba_SetsAlpha()
    {
        var pixel = PixelInfo.FromRgba(100, 200, 300, 400);

        await Assert.That(pixel.Alpha).IsEqualTo(400.0);
        await Assert.That(pixel.HasAlpha).IsTrue();
    }

    [Test]
    public async Task PixelInfo_FromRgb_HasNoAlpha()
    {
        var pixel = PixelInfo.FromRgb(100, 200, 300);
        await Assert.That(pixel.HasAlpha).IsFalse();
    }

    [Test]
    public async Task PixelInfo_IsFuzzyEqual_IdenticalPixels()
    {
        var a = PixelInfo.FromRgb(10000, 20000, 30000);
        var b = PixelInfo.FromRgb(10000, 20000, 30000);

        await Assert.That(a.IsFuzzyEqual(b, 0.0)).IsTrue();
    }

    [Test]
    public async Task PixelInfo_IsFuzzyEqual_WithinTolerance()
    {
        var a = PixelInfo.FromRgb(10000, 20000, 30000);
        var b = PixelInfo.FromRgb(10010, 20010, 30010);

        // Fuzz of 100 should accept small differences
        await Assert.That(a.IsFuzzyEqual(b, 100.0)).IsTrue();
    }

    [Test]
    public async Task PixelInfo_IsFuzzyEqual_OutsideTolerance()
    {
        var a = PixelInfo.FromRgb(0, 0, 0);
        var b = PixelInfo.FromRgb(65535, 65535, 65535);

        await Assert.That(a.IsFuzzyEqual(b, 1.0)).IsFalse();
    }

    [Test]
    public async Task PixelChannelMap_StoresValues()
    {
        var map = new PixelChannelMap
        {
            Channel = PixelChannel.Red,
            Traits = PixelTrait.Update,
            Offset = 0
        };

        await Assert.That(map.Channel).IsEqualTo(PixelChannel.Red);
        await Assert.That(map.Traits).IsEqualTo(PixelTrait.Update);
        await Assert.That(map.Offset).IsEqualTo(0);
    }

    [Test]
    public async Task PixelPacket_StoresValues()
    {
        var packet = new PixelPacket
        {
            Red = 255,
            Green = 128,
            Blue = 0,
            Alpha = 255,
            Black = 0
        };

        await Assert.That(packet.Red).IsEqualTo(255u);
        await Assert.That(packet.Green).IsEqualTo(128u);
        await Assert.That(packet.Blue).IsEqualTo(0u);
    }
}
