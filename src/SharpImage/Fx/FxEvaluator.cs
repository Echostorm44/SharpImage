// SharpImage — FX expression evaluator.
// Applies per-pixel math expressions to images, inspired by ImageMagick -fx.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Fx;

/// <summary>
/// Evaluates per-pixel math expressions on images.
/// Supports arithmetic, comparisons, ternary, 30+ math functions,
/// channel accessors (u.r, u.g, u.b, u.a), multi-image references,
/// and coordinate variables (i, j, w, h).
/// </summary>
public static class FxEvaluator
{
    /// <summary>
    /// Apply an FX expression to a single image.
    /// The expression is evaluated per-pixel; 'u' refers to the source pixel (normalized 0-1).
    /// Returns a new image with the expression result written to all color channels.
    /// </summary>
    public static ImageFrame Apply(ImageFrame source, string expression)
        => Apply([source], expression);

    /// <summary>
    /// Apply an FX expression using multiple source images.
    /// u[0] or u refers to the first image, u[1] to the second, etc.
    /// The output dimensions match the first image.
    /// </summary>
    public static ImageFrame Apply(ImageFrame[] sources, string expression)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0) throw new ArgumentException("At least one source image is required.", nameof(sources));
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        // Parse expression once
        var lexer = new FxLexer(expression);
        var tokens = lexer.Tokenize();
        var parser = new FxParser(tokens);
        var ast = parser.Parse();

        var primary = sources[0];
        int width = (int)primary.Columns;
        int height = (int)primary.Rows;
        int channels = primary.NumberOfChannels;
        bool hasAlpha = primary.HasAlpha;
        double scale = 1.0 / Quantum.MaxValue;

        // Pre-normalize all source images to double[0..1]
        double[][] normalizedPixels = new double[sources.Length][];
        int[] widths = new int[sources.Length];
        int[] heights = new int[sources.Length];
        int[] channelCounts = new int[sources.Length];

        for (int img = 0; img < sources.Length; img++)
        {
            var src = sources[img];
            int w = (int)src.Columns;
            int h = (int)src.Rows;
            int ch = src.NumberOfChannels;
            widths[img] = w;
            heights[img] = h;
            channelCounts[img] = ch;

            double[] pixels = new double[w * h * ch];
            for (int y = 0; y < h; y++)
            {
                var row = src.GetPixelRow(y);
                int rowBase = y * w * ch;
                for (int x = 0; x < w; x++)
                {
                    int srcOff = x * ch;
                    int dstOff = rowBase + x * ch;
                    for (int c = 0; c < ch; c++)
                        pixels[dstOff + c] = row[srcOff + c] * scale;
                }
            }
            normalizedPixels[img] = pixels;
        }

        // Create output
        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, primary.Colorspace, hasAlpha);

        // Evaluate per pixel using Parallel.For for row-level parallelism
        Parallel.For(0, height, y =>
        {
            var ctx = new FxContext(normalizedPixels, widths, heights, channelCounts, hasAlpha);
            ctx.Y = y;
            ctx.CurrentImageIndex = 0;

            var outRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                ctx.X = x;
                double value = ast.Evaluate(ref ctx);

                // Clamp to [0,1] then scale to quantum
                ushort qval = Quantum.ScaleFromDouble(Math.Clamp(value, 0.0, 1.0));

                int off = x * channels;
                for (int c = 0; c < channels; c++)
                {
                    if (hasAlpha && c == channels - 1)
                    {
                        // Preserve alpha from source
                        outRow[off + c] = (ushort)(normalizedPixels[0][(y * width + x) * channels + c] * Quantum.MaxValue);
                    }
                    else
                    {
                        outRow[off + c] = qval;
                    }
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Apply an FX expression per-channel (each color channel gets the expression result independently).
    /// Channel-specific expressions use u.r, u.g, u.b for individual channel access.
    /// Semicolons separate per-channel expressions: "u.r * 2; u.g; u.b * 0.5"
    /// </summary>
    public static ImageFrame ApplyPerChannel(ImageFrame source, string redExpr, string greenExpr, string blueExpr,
        string? alphaExpr = null)
    {
        int width = (int)source.Columns;
        int height = (int)source.Rows;
        int channels = source.NumberOfChannels;
        bool hasAlpha = source.HasAlpha;
        double scale = 1.0 / Quantum.MaxValue;

        // Parse all expressions
        var rAst = ParseExpression(redExpr);
        var gAst = channels >= 3 ? ParseExpression(greenExpr) : rAst;
        var bAst = channels >= 3 ? ParseExpression(blueExpr) : rAst;
        var aAst = alphaExpr != null && hasAlpha ? ParseExpression(alphaExpr) : null;

        // Normalize source
        double[] pixels = new double[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            var row = source.GetPixelRow(y);
            int rowBase = y * width * channels;
            for (int x = 0; x < width; x++)
            {
                int srcOff = x * channels;
                int dstOff = rowBase + x * channels;
                for (int c = 0; c < channels; c++)
                    pixels[dstOff + c] = row[srcOff + c] * scale;
            }
        }

        double[][] normalizedPixels = [pixels];
        int[] widths = [width];
        int[] heights = [height];
        int[] channelCounts = [channels];

        var result = new ImageFrame();
        result.Initialize((uint)width, (uint)height, source.Colorspace, hasAlpha);

        Parallel.For(0, height, y =>
        {
            var ctx = new FxContext(normalizedPixels, widths, heights, channelCounts, hasAlpha);
            ctx.Y = y;
            var outRow = result.GetPixelRowForWrite(y);

            for (int x = 0; x < width; x++)
            {
                ctx.X = x;
                int off = x * channels;

                double r = Math.Clamp(rAst.Evaluate(ref ctx), 0.0, 1.0);
                outRow[off] = Quantum.ScaleFromDouble(r);

                if (channels >= 3)
                {
                    double g = Math.Clamp(gAst.Evaluate(ref ctx), 0.0, 1.0);
                    double b = Math.Clamp(bAst.Evaluate(ref ctx), 0.0, 1.0);
                    outRow[off + 1] = Quantum.ScaleFromDouble(g);
                    outRow[off + 2] = Quantum.ScaleFromDouble(b);
                }

                if (hasAlpha)
                {
                    int alphaIdx = off + channels - 1;
                    if (aAst != null)
                        outRow[alphaIdx] = Quantum.ScaleFromDouble(Math.Clamp(aAst.Evaluate(ref ctx), 0.0, 1.0));
                    else
                        outRow[alphaIdx] = (ushort)(pixels[(y * width + x) * channels + channels - 1] * Quantum.MaxValue);
                }
            }
        });

        return result;
    }

    private static FxNode ParseExpression(string expr)
    {
        var lexer = new FxLexer(expr);
        var tokens = lexer.Tokenize();
        var parser = new FxParser(tokens);
        return parser.Parse();
    }
}
