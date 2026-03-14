using SharpImage.Core;

namespace SharpImage.Tests.Core;

public class GeometryTests
{
    [Test]
    public async Task RectangleInfo_Area_ReturnsCorrectValue()
    {
        var rect = new RectangleInfo(100, 50, 10, 20);
        await Assert.That(rect.Area).IsEqualTo(5000L);
    }

    [Test]
    public async Task RectangleInfo_IsEmpty_WhenZeroWidth()
    {
        var rect = new RectangleInfo(0, 50);
        await Assert.That(rect.IsEmpty).IsTrue();
    }

    [Test]
    public async Task RectangleInfo_IsNotEmpty_WhenPositiveDimensions()
    {
        var rect = new RectangleInfo(10, 10);
        await Assert.That(rect.IsEmpty).IsFalse();
    }

    [Test]
    public async Task RectangleInfo_Intersect_OverlappingRects()
    {
        var a = new RectangleInfo(100, 100, 0, 0);
        var b = new RectangleInfo(100, 100, 50, 50);
        var result = a.Intersect(b);

        await Assert.That(result.X).IsEqualTo(50L);
        await Assert.That(result.Y).IsEqualTo(50L);
        await Assert.That(result.Width).IsEqualTo(50L);
        await Assert.That(result.Height).IsEqualTo(50L);
    }

    [Test]
    public async Task RectangleInfo_Intersect_NonOverlapping_ReturnsEmpty()
    {
        var a = new RectangleInfo(10, 10, 0, 0);
        var b = new RectangleInfo(10, 10, 100, 100);
        var result = a.Intersect(b);

        await Assert.That(result.IsEmpty).IsTrue();
    }

    [Test]
    public async Task RectangleInfo_ToString_FormatsCorrectly()
    {
        var rect = new RectangleInfo(640, 480, 10, 20);
        await Assert.That(rect.ToString()).IsEqualTo("640x480+10+20");
    }

    [Test]
    public async Task PointInfo_Constructor_SetsValues()
    {
        var point = new PointInfo(3.14, 2.71);
        await Assert.That(point.X).IsEqualTo(3.14);
        await Assert.That(point.Y).IsEqualTo(2.71);
    }

    [Test]
    public async Task AffineMatrix_Identity_NoTransform()
    {
        var identity = AffineMatrix.Identity;
        var point = new PointInfo(42.0, 17.0);
        var result = identity.Transform(point);

        await Assert.That(result.X).IsEqualTo(42.0);
        await Assert.That(result.Y).IsEqualTo(17.0);
    }

    [Test]
    public async Task AffineMatrix_Translation()
    {
        var translate = AffineMatrix.Identity;
        translate.Tx = 10.0;
        translate.Ty = 20.0;

        var point = new PointInfo(5.0, 5.0);
        var result = translate.Transform(point);

        await Assert.That(result.X).IsEqualTo(15.0);
        await Assert.That(result.Y).IsEqualTo(25.0);
    }

    [Test]
    public async Task AffineMatrix_Scale()
    {
        var scale = AffineMatrix.Identity;
        scale.Sx = 2.0;
        scale.Sy = 3.0;

        var point = new PointInfo(10.0, 10.0);
        var result = scale.Transform(point);

        await Assert.That(result.X).IsEqualTo(20.0);
        await Assert.That(result.Y).IsEqualTo(30.0);
    }

    [Test]
    public async Task AffineMatrix_Multiply_Identity()
    {
        var identity = AffineMatrix.Identity;
        var translate = AffineMatrix.Identity;
        translate.Tx = 5.0;
        translate.Ty = 10.0;

        var result = identity.Multiply(translate);

        await Assert.That(result.Tx).IsEqualTo(5.0);
        await Assert.That(result.Ty).IsEqualTo(10.0);
        await Assert.That(result.Sx).IsEqualTo(1.0);
        await Assert.That(result.Sy).IsEqualTo(1.0);
    }

    [Test]
    public async Task AffineMatrix_Multiply_ScaleThenTranslate()
    {
        var scale = AffineMatrix.Identity;
        scale.Sx = 2.0;
        scale.Sy = 2.0;

        var translate = AffineMatrix.Identity;
        translate.Tx = 10.0;
        translate.Ty = 20.0;

        // Scale first, then translate
        var combined = scale.Multiply(translate);
        var point = new PointInfo(5.0, 5.0);
        var result = combined.Transform(point);

        // 5*2 + 10 = 20, 5*2 + 20 = 30
        await Assert.That(result.X).IsEqualTo(20.0);
        await Assert.That(result.Y).IsEqualTo(30.0);
    }
}
