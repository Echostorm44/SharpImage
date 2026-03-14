// SharpImage Tests — FX expression evaluator tests.

using SharpImage.Core;
using SharpImage.Fx;
using SharpImage.Image;

namespace SharpImage.Tests.Fx;

public class FxEvaluatorTests
{
    private static ImageFrame CreateUniformImage(int width, int height, ushort r, ushort g, ushort b)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int off = x * 3;
                row[off] = r;
                row[off + 1] = g;
                row[off + 2] = b;
            }
        }
        return frame;
    }

    private static ImageFrame CreateGradientImage(int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                ushort val = (ushort)(x * Quantum.MaxValue / (width - 1));
                int off = x * 3;
                row[off] = val;
                row[off + 1] = val;
                row[off + 2] = val;
            }
        }
        return frame;
    }

    private static double GetNormalizedPixel(ImageFrame frame, int x, int y, int channel)
    {
        var row = frame.GetPixelRow(y);
        return row[x * frame.NumberOfChannels + channel] / (double)Quantum.MaxValue;
    }

    // ══════════════════════════════════════════════════════════════
    //  Basic Arithmetic
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Constant_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "0.5");
        double val = GetNormalizedPixel(result, 5, 5, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.001);
    }

    [Test]
    public async Task Identity_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u");
        double val = GetNormalizedPixel(result, 5, 5, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    [Test]
    public async Task Negate_Expression()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "1.0 - u");
        double orig = GetNormalizedPixel(source, 0, 0, 0);
        double negated = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(negated - (1.0 - orig))).IsLessThan(0.01);
    }

    [Test]
    public async Task Add_Expression()
    {
        var source = CreateUniformImage(10, 10, 16384, 16384, 16384);
        var result = FxEvaluator.Apply(source, "u + 0.25");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.45);
    }

    [Test]
    public async Task Multiply_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u * 2");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.95);
    }

    [Test]
    public async Task Division_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u / 2");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.25)).IsLessThan(0.01);
    }

    [Test]
    public async Task Power_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u ^ 2");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.25)).IsLessThan(0.01);
    }

    [Test]
    public async Task Modulo_Expression()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "u % 0.25");
        double orig = GetNormalizedPixel(source, 0, 0, 0);
        double expected = orig % 0.25;
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - expected)).IsLessThan(0.01);
    }

    [Test]
    public async Task DivisionByZero_ReturnsZero()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u / 0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsEqualTo(0.0);
    }

    // ══════════════════════════════════════════════════════════════
    //  Comparison & Logical
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Comparison_GreaterThan()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "u > 0.5 ? 1.0 : 0.0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task Comparison_LessThan()
    {
        var source = CreateUniformImage(10, 10, 16384, 16384, 16384);
        var result = FxEvaluator.Apply(source, "u < 0.5 ? 1.0 : 0.0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task LogicalAnd()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "u > 0.3 && u < 0.9 ? 1.0 : 0.0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task LogicalOr()
    {
        var source = CreateUniformImage(10, 10, 65535, 65535, 65535);
        var result = FxEvaluator.Apply(source, "u < 0.1 || u > 0.9 ? 1.0 : 0.0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task LogicalNot()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "!u");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    // ══════════════════════════════════════════════════════════════
    //  Ternary
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Ternary_TrueBranch()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "u > 0.5 ? 0.9 : 0.1");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.9)).IsLessThan(0.01);
    }

    [Test]
    public async Task Ternary_FalseBranch()
    {
        var source = CreateUniformImage(10, 10, 16384, 16384, 16384);
        var result = FxEvaluator.Apply(source, "u > 0.5 ? 0.9 : 0.1");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.1)).IsLessThan(0.01);
    }

    [Test]
    public async Task NestedTernary()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u < 0.3 ? 0.0 : u < 0.7 ? 0.5 : 1.0");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Math Functions
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Sin_Function()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "sin(u * pi)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        // sin(0.5 * pi) = 1.0
        await Assert.That(Math.Abs(val - 1.0)).IsLessThan(0.01);
    }

    [Test]
    public async Task Cos_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "cos(0)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 1.0)).IsLessThan(0.01);
    }

    [Test]
    public async Task Abs_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "abs(-0.7)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.7)).IsLessThan(0.01);
    }

    [Test]
    public async Task Sqrt_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "sqrt(0.25)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    [Test]
    public async Task Min_Max_Functions()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "max(min(u, 0.3), 0.1)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.3)).IsLessThan(0.01);
    }

    [Test]
    public async Task Clamp_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "clamp(0.8, 0.2, 0.6)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.6)).IsLessThan(0.01);
    }

    [Test]
    public async Task Floor_Ceil_Round()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var floor = FxEvaluator.Apply(source, "floor(0.7)");
        var ceil = FxEvaluator.Apply(source, "ceil(0.3)");
        double fv = GetNormalizedPixel(floor, 0, 0, 0);
        double cv = GetNormalizedPixel(ceil, 0, 0, 0);
        await Assert.That(fv).IsEqualTo(0.0);
        await Assert.That(cv).IsGreaterThan(0.9);
    }

    [Test]
    public async Task Pow_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "pow(0.5, 2)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.25)).IsLessThan(0.01);
    }

    [Test]
    public async Task If_Function()
    {
        var source = CreateUniformImage(10, 10, 49152, 49152, 49152);
        var result = FxEvaluator.Apply(source, "if(u > 0.5, 0.8, 0.2)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.8)).IsLessThan(0.01);
    }

    [Test]
    public async Task Sinc_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "sinc(0)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 1.0)).IsLessThan(0.01);
    }

    [Test]
    public async Task Gauss_Function()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "gauss(0)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 1.0)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Channel Access
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Channel_Red()
    {
        var source = CreateUniformImage(10, 10, 65535, 0, 0);
        var result = FxEvaluator.Apply(source, "u.r");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task Channel_Green()
    {
        var source = CreateUniformImage(10, 10, 0, 65535, 0);
        var result = FxEvaluator.Apply(source, "u.g");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task Channel_Blue()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 65535);
        var result = FxEvaluator.Apply(source, "u.b");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    [Test]
    public async Task Channel_Swap_RGB()
    {
        var source = CreateUniformImage(10, 10, 65535, 0, 0);
        var result = FxEvaluator.Apply(source, "u.b");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Coordinate Variables
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task CoordinateVariable_I()
    {
        var source = CreateUniformImage(100, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "i / w");
        double left = GetNormalizedPixel(result, 0, 0, 0);
        double right = GetNormalizedPixel(result, 99, 0, 0);
        await Assert.That(left).IsLessThan(0.01);
        await Assert.That(right).IsGreaterThan(0.95);
    }

    [Test]
    public async Task CoordinateVariable_J()
    {
        var source = CreateUniformImage(10, 100, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "j / h");
        double top = GetNormalizedPixel(result, 0, 0, 0);
        double bottom = GetNormalizedPixel(result, 0, 99, 0);
        await Assert.That(top).IsLessThan(0.01);
        await Assert.That(bottom).IsGreaterThan(0.95);
    }

    // ══════════════════════════════════════════════════════════════
    //  Constants
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Constant_Pi()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "pi / 10");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - Math.PI / 10.0)).IsLessThan(0.01);
    }

    [Test]
    public async Task Constant_E()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "e / 10");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - Math.E / 10.0)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Multi-Image
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task MultiImage_Blend()
    {
        var black = CreateUniformImage(10, 10, 0, 0, 0);
        var white = CreateUniformImage(10, 10, 65535, 65535, 65535);
        var result = FxEvaluator.Apply([black, white], "0.5 * u[0] + 0.5 * u[1]");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    [Test]
    public async Task MultiImage_ChannelAccess()
    {
        var red = CreateUniformImage(10, 10, 65535, 0, 0);
        var green = CreateUniformImage(10, 10, 0, 65535, 0);
        var result = FxEvaluator.Apply([red, green], "u[1].g");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(val).IsGreaterThan(0.9);
    }

    // ══════════════════════════════════════════════════════════════
    //  Per-Channel Mode
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task PerChannel_SwapRB()
    {
        var source = CreateUniformImage(10, 10, 65535, 32768, 0);
        var result = FxEvaluator.ApplyPerChannel(source, "u.b", "u.g", "u.r");
        double r = GetNormalizedPixel(result, 0, 0, 0);
        double b = GetNormalizedPixel(result, 0, 0, 2);
        await Assert.That(r).IsLessThan(0.01);
        await Assert.That(b).IsGreaterThan(0.9);
    }

    // ══════════════════════════════════════════════════════════════
    //  Complex Expressions
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Posterize_Expression()
    {
        var source = CreateGradientImage(100, 10);
        var result = FxEvaluator.Apply(source, "floor(u * 4) / 4");
        double val = GetNormalizedPixel(result, 50, 0, 0);
        double remainder = val % 0.25;
        await Assert.That(remainder).IsLessThan(0.02);
    }

    [Test]
    public async Task Threshold_Expression()
    {
        var source = CreateGradientImage(100, 10);
        var result = FxEvaluator.Apply(source, "u > 0.5 ? 1 : 0");
        double dark = GetNormalizedPixel(result, 10, 0, 0);
        double bright = GetNormalizedPixel(result, 90, 0, 0);
        await Assert.That(dark).IsLessThan(0.01);
        await Assert.That(bright).IsGreaterThan(0.99);
    }

    [Test]
    public async Task GammaCorrection_Expression()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u ^ 0.4545");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        double expected = Math.Pow(0.5, 0.4545);
        await Assert.That(Math.Abs(val - expected)).IsLessThan(0.01);
    }

    [Test]
    public async Task Checkerboard_Expression()
    {
        var source = CreateUniformImage(20, 20, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "(floor(i / 5) + floor(j / 5)) % 2");
        double corner = GetNormalizedPixel(result, 0, 0, 0);
        double check = GetNormalizedPixel(result, 5, 0, 0);
        await Assert.That(corner).IsLessThan(0.01);
        await Assert.That(check).IsGreaterThan(0.9);
    }

    // ══════════════════════════════════════════════════════════════
    //  Semicolon Sequences
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Semicolon_LastValueWins()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "0.1; 0.5; 0.9");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.9)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Pixel Lookup: p(x,y)
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task PixelLookup_P()
    {
        var source = CreateGradientImage(100, 10);
        var result = FxEvaluator.Apply(source, "p(0, 0)");
        double val = GetNormalizedPixel(result, 50, 5, 0);
        await Assert.That(val).IsLessThan(0.05);
    }

    // ══════════════════════════════════════════════════════════════
    //  Error Handling
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Parse_EmptyExpression_Throws()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        await Assert.That(() => FxEvaluator.Apply(source, ""))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Parse_InvalidSyntax_Throws()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        await Assert.That(() => FxEvaluator.Apply(source, "u + "))
            .ThrowsExactly<FxParseException>();
    }

    [Test]
    public async Task Parse_UnknownFunction_Throws()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        await Assert.That(() => FxEvaluator.Apply(source, "unknown(u)"))
            .ThrowsExactly<FxParseException>();
    }

    [Test]
    public async Task Parse_UnbalancedParen_Throws()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        await Assert.That(() => FxEvaluator.Apply(source, "sin(u"))
            .ThrowsExactly<FxParseException>();
    }

    [Test]
    public async Task Parse_WrongArgCount_Throws()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        await Assert.That(() => FxEvaluator.Apply(source, "sin(u, 1)"))
            .ThrowsExactly<FxParseException>();
    }

    // ══════════════════════════════════════════════════════════════
    //  Dimension Preservation
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Apply_PreservesDimensions()
    {
        var source = CreateUniformImage(123, 45, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u * 0.5");
        await Assert.That(result.Columns).IsEqualTo(source.Columns);
        await Assert.That(result.Rows).IsEqualTo(source.Rows);
    }

    [Test]
    public async Task Apply_PreservesColorspace()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "u");
        await Assert.That(result.Colorspace).IsEqualTo(source.Colorspace);
    }

    // ══════════════════════════════════════════════════════════════
    //  Grayscale Image
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Apply_GrayscaleImage()
    {
        var source = new ImageFrame();
        source.Initialize(10, 10, ColorspaceType.Gray, false);
        for (int y = 0; y < 10; y++)
        {
            var row = source.GetPixelRowForWrite(y);
            for (int x = 0; x < 10; x++)
                row[x] = 32768;
        }

        var result = FxEvaluator.Apply(source, "1.0 - u");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Unary operators
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task UnaryMinus()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "abs(-0.5)");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    [Test]
    public async Task UnaryPlus()
    {
        var source = CreateUniformImage(10, 10, 32768, 32768, 32768);
        var result = FxEvaluator.Apply(source, "+u");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.5)).IsLessThan(0.01);
    }

    // ══════════════════════════════════════════════════════════════
    //  Parentheses & Precedence
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task Precedence_MultiplyBeforeAdd()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "0.1 + 0.2 * 0.5");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.2)).IsLessThan(0.01);
    }

    [Test]
    public async Task Parentheses_OverridePrecedence()
    {
        var source = CreateUniformImage(10, 10, 0, 0, 0);
        var result = FxEvaluator.Apply(source, "(0.1 + 0.2) * 0.5");
        double val = GetNormalizedPixel(result, 0, 0, 0);
        await Assert.That(Math.Abs(val - 0.15)).IsLessThan(0.01);
    }
}
