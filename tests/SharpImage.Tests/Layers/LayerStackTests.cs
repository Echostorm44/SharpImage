using SharpImage.Core;
using SharpImage.Image;
using SharpImage.Layers;
using SharpImage.Transform;

namespace SharpImage.Tests.Layers;

public class LayerStackTests
{
    private static ImageFrame CreateSolid(uint w, uint h, ushort r, ushort g, ushort b, bool alpha = false, ushort a = 65535)
    {
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.RGB, alpha);
        int ch = frame.NumberOfChannels;
        for (int y = 0; y < (int)h; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < (int)w; x++)
            {
                int off = x * ch;
                row[off] = r; row[off + 1] = g; row[off + 2] = b;
                if (alpha) row[off + 3] = a;
            }
        }
        return frame;
    }

    // ─── Basic Stack Tests ─────────────────────────────────────────

    [Test]
    public async Task LayerStack_EmptyFlattenProducesTransparent()
    {
        using var stack = new LayerStack(20, 15);
        var result = stack.Flatten();

        await Assert.That(result.Columns).IsEqualTo(20u);
        await Assert.That(result.Rows).IsEqualTo(15u);
        await Assert.That(result.HasAlpha).IsTrue();

        var arr = result.GetPixelRow(7).ToArray();
        await Assert.That(arr[40 + 3]).IsEqualTo((ushort)0);
        result.Dispose();
    }

    [Test]
    public async Task LayerStack_SingleLayerFlatten()
    {
        using var stack = new LayerStack(20, 15);
        var red = CreateSolid(20, 15, Quantum.MaxValue, 0, 0, true);
        stack.AddLayer(new Layer("Red") { Content = red });

        var result = stack.Flatten();
        var arr = result.GetPixelRow(7).ToArray();
        await Assert.That(arr[40]).IsEqualTo(Quantum.MaxValue);
        await Assert.That(arr[41]).IsEqualTo((ushort)0);
        await Assert.That(arr[42]).IsEqualTo((ushort)0);
        await Assert.That(arr[43]).IsEqualTo(Quantum.MaxValue);
        result.Dispose();
    }

    [Test]
    public async Task LayerStack_TwoLayerOverComposite()
    {
        using var stack = new LayerStack(10, 10);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true, (ushort)(Quantum.MaxValue / 2));

        stack.AddLayer(new Layer("Blue") { Content = blue });
        stack.AddLayer(new Layer("Red") { Content = red });

        var result = stack.Flatten();
        var arr = result.GetPixelRow(5).ToArray();

        int rVal = arr[20];
        int bVal = arr[22];
        await Assert.That(rVal).IsGreaterThan(0);
        await Assert.That(bVal).IsGreaterThan(0);
        result.Dispose();
    }

    // ─── Opacity Tests ─────────────────────────────────────────────

    [Test]
    public async Task LayerStack_OpacityZeroMeansInvisible()
    {
        using var stack = new LayerStack(10, 10);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true);

        stack.AddLayer(new Layer("Blue") { Content = blue });
        stack.AddLayer(new Layer("Red") { Content = red, Opacity = 0.0 });

        var result = stack.Flatten();
        var arr = result.GetPixelRow(5).ToArray();
        await Assert.That(arr[20]).IsEqualTo((ushort)0);
        await Assert.That(arr[22]).IsEqualTo(Quantum.MaxValue);
        result.Dispose();
    }

    // ─── Visibility Tests ──────────────────────────────────────────

    [Test]
    public async Task LayerStack_InvisibleLayerSkipped()
    {
        using var stack = new LayerStack(10, 10);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true);

        stack.AddLayer(new Layer("Blue") { Content = blue });
        stack.AddLayer(new Layer("Red") { Content = red, Visible = false });

        var result = stack.Flatten();
        var arr = result.GetPixelRow(5).ToArray();
        await Assert.That(arr[20]).IsEqualTo((ushort)0);
        await Assert.That(arr[22]).IsEqualTo(Quantum.MaxValue);
        result.Dispose();
    }

    // ─── Layer Mask Tests ──────────────────────────────────────────

    [Test]
    public async Task LayerStack_MaskControlsVisibility()
    {
        using var stack = new LayerStack(20, 10);
        var blue = CreateSolid(20, 10, 0, 0, Quantum.MaxValue, true);
        var red = CreateSolid(20, 10, Quantum.MaxValue, 0, 0, true);

        var mask = new ImageFrame();
        mask.Initialize(20, 10, ColorspaceType.Gray, false);
        for (int y = 0; y < 10; y++)
        {
            var row = mask.GetPixelRowForWrite(y);
            for (int x = 0; x < 20; x++)
                row[x] = x < 10 ? Quantum.MaxValue : (ushort)0;
        }

        stack.AddLayer(new Layer("Blue") { Content = blue });
        stack.AddLayer(new Layer("Red") { Content = red, Mask = mask });

        var result = stack.Flatten();
        var leftArr = result.GetPixelRow(5).ToArray();

        // Left half: red
        await Assert.That(leftArr[12]).IsEqualTo(Quantum.MaxValue); // x=3, R

        // Right half: blue (red masked out)
        await Assert.That(leftArr[60]).IsEqualTo((ushort)0);        // x=15, R
        await Assert.That(leftArr[62]).IsEqualTo(Quantum.MaxValue); // x=15, B
        result.Dispose();
    }

    // ─── Adjustment Layer Tests ────────────────────────────────────

    [Test]
    public async Task AdjustmentLayer_AppliesTransform()
    {
        using var stack = new LayerStack(10, 10);
        var white = CreateSolid(10, 10, Quantum.MaxValue, Quantum.MaxValue, Quantum.MaxValue, true);
        stack.AddLayer(new Layer("White") { Content = white });

        stack.AddLayer(new AdjustmentLayer("Invert",
            img => SharpImage.Effects.ColorAdjust.Invert(img)));

        var result = stack.Flatten();
        var arr = result.GetPixelRow(5).ToArray();
        await Assert.That(arr[20]).IsEqualTo((ushort)0);
        await Assert.That(arr[21]).IsEqualTo((ushort)0);
        await Assert.That(arr[22]).IsEqualTo((ushort)0);
        result.Dispose();
    }

    // ─── Merge Tests ───────────────────────────────────────────────

    [Test]
    public async Task MergeLayers_ReducesCount()
    {
        using var stack = new LayerStack(10, 10);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true);
        var green = CreateSolid(10, 10, 0, Quantum.MaxValue, 0, true);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);

        stack.AddLayer(new Layer("Red") { Content = red });
        stack.AddLayer(new Layer("Green") { Content = green });
        stack.AddLayer(new Layer("Blue") { Content = blue });

        await Assert.That(stack.Count).IsEqualTo(3);
        stack.MergeLayers(0, 2);
        await Assert.That(stack.Count).IsEqualTo(2);
    }

    // ─── Layer Ordering Tests ──────────────────────────────────────

    [Test]
    public async Task MoveLayer_ChangesOrder()
    {
        using var stack = new LayerStack(10, 10);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);

        var redLayer = new Layer("Red") { Content = red };
        var blueLayer = new Layer("Blue") { Content = blue };

        stack.AddLayer(redLayer);
        stack.AddLayer(blueLayer);

        await Assert.That(stack[0].Name).IsEqualTo("Red");
        stack.MoveLayer(0, 1);
        await Assert.That(stack[0].Name).IsEqualTo("Blue");
        await Assert.That(stack[1].Name).IsEqualTo("Red");
    }

    [Test]
    public async Task InsertLayer_AddsAtPosition()
    {
        using var stack = new LayerStack(10, 10);
        var red = CreateSolid(10, 10, Quantum.MaxValue, 0, 0, true);
        var blue = CreateSolid(10, 10, 0, 0, Quantum.MaxValue, true);
        var green = CreateSolid(10, 10, 0, Quantum.MaxValue, 0, true);

        stack.AddLayer(new Layer("Red") { Content = red });
        stack.AddLayer(new Layer("Blue") { Content = blue });
        stack.InsertLayer(1, new Layer("Green") { Content = green });

        await Assert.That(stack.Count).IsEqualTo(3);
        await Assert.That(stack[1].Name).IsEqualTo("Green");
    }

    // ─── Clipping Group Test ───────────────────────────────────────

    [Test]
    public async Task ClippedLayer_RestrictedToParentAlpha()
    {
        using var stack = new LayerStack(20, 10);

        var baseImg = new ImageFrame();
        baseImg.Initialize(20, 10, ColorspaceType.RGB, true);
        for (int y = 0; y < 10; y++)
        {
            var row = baseImg.GetPixelRowForWrite(y);
            for (int x = 0; x < 20; x++)
            {
                int off = x * 4;
                row[off] = Quantum.MaxValue; row[off + 1] = 0; row[off + 2] = 0;
                row[off + 3] = x < 10 ? Quantum.MaxValue : (ushort)0;
            }
        }

        var green = CreateSolid(20, 10, 0, Quantum.MaxValue, 0, true);

        stack.AddLayer(new Layer("Base") { Content = baseImg });
        stack.AddLayer(new Layer("Clipped Green") { Content = green, ClippedToBelow = true });

        var result = stack.Flatten();
        var arr = result.GetPixelRow(5).ToArray();

        // Left half: green (clipped visible)
        await Assert.That(arr[12 + 1]).IsGreaterThan((ushort)0);

        // Right half: transparent
        int rightAlpha = arr[60 + 3];
        await Assert.That(rightAlpha).IsEqualTo((ushort)0);
        result.Dispose();
    }
}
