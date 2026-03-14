// SharpImage CLI — Effects and color adjustment commands.

using SharpImage.Effects;
using SharpImage.Image;
using System.CommandLine;

namespace SharpImage.Cli;

public static class EffectsCommands
{
    public static Command CreateBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Gaussian blur sigma (radius)", DefaultValueFactory = _ => 2.0 };
        var boxOpt = new Option<bool>("--box") { Description = "Use box blur instead of Gaussian" };
        var cmd = new Command("blur", """
            Apply blur to an image.
            Default is Gaussian blur. Use --box for box (uniform) blur.

            Examples:
              sharpimage blur photo.jpg --sigma 3.0 blurred.png
              sharpimage blur photo.jpg --box --sigma 5 boxblur.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(boxOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            bool box = parseResult.GetValue(boxOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            if (box)
            {
                CliOutput.RunPipeline(input, output, $"Box blur (radius={sigma:F0})",
                                img => ConvolutionFilters.BoxBlur(img, (int)sigma));
            }
            else
            {
                CliOutput.RunPipeline(input, output, $"Gaussian blur (σ={sigma:F1})",
                                img => ConvolutionFilters.GaussianBlur(img, sigma));
            }
        });
        return cmd;
    }

    public static Command CreateSharpenCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Blur sigma for unsharp mask", DefaultValueFactory = _ => 1.0 };
        var amountOpt = new Option<double>("-a", "--amount") { Description = "Sharpening amount (1.0 = subtle, 3.0 = strong)", DefaultValueFactory = _ => 1.5 };
        var cmd = new Command("sharpen", """
            Sharpen an image using unsharp masking.
            Works by subtracting a blurred version and adding the difference back.

            Examples:
              sharpimage sharpen photo.jpg --sigma 1.0 --amount 2.0 sharp.png
              sharpimage sharpen soft.png sharp.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(amountOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            double amount = parseResult.GetValue(amountOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Sharpening (σ={sigma:F1}, amount={amount:F1})",
                img => ConvolutionFilters.Sharpen(img, sigma, amount));
        });
        return cmd;
    }

    public static Command CreateUnsharpMaskCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Gaussian blur sigma (sharpening radius)", DefaultValueFactory = _ => 1.0 };
        var amountOpt = new Option<double>("-a", "--amount") { Description = "Sharpening strength multiplier", DefaultValueFactory = _ => 1.5 };
        var thresholdOpt = new Option<double>("-t", "--threshold") { Description = "Minimum difference (0–1) to apply sharpening. 0 = sharpen everything.", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("unsharpmask", """
            Apply unsharp mask sharpening with threshold control.
            Classic formula: result = original + amount * (original - blurred)
            Threshold prevents noise amplification in smooth areas.

            Examples:
              sharpimage unsharpmask photo.jpg -s 2.0 -a 1.5 -t 0.03 sharp.png
              sharpimage unsharpmask soft.png sharp.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(amountOpt);
        cmd.Add(thresholdOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            double amount = parseResult.GetValue(amountOpt);
            double threshold = parseResult.GetValue(thresholdOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Unsharp Mask (σ={sigma:F1}, amount={amount:F1}, threshold={threshold:F2})",
                img => ConvolutionFilters.UnsharpMask(img, sigma, amount, threshold));
        });
        return cmd;
    }

    public static Command CreateEdgeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("edge", """
            Detect edges using Sobel operator.
            Produces a grayscale image where edges are bright.

            Examples:
              sharpimage edge photo.jpg edges.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Edge detection (Sobel)", ConvolutionFilters.EdgeDetect);
        });
        return cmd;
    }

    public static Command CreateEmbossCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("emboss", """
            Apply emboss effect to an image.
            Creates a raised, 3D appearance.

            Examples:
              sharpimage emboss photo.jpg embossed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Embossing", ConvolutionFilters.Emboss);
        });
        return cmd;
    }

    #region Color Adjustments

    public static Command CreateBrightnessCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var factorOpt = new Option<double>("-f", "--factor") { Description = "Brightness factor (0.0=black, 1.0=unchanged, 2.0=double)", DefaultValueFactory = _ => 1.2 };
        var cmd = new Command("brightness", """
            Adjust image brightness by a multiplicative factor.

            Examples:
              sharpimage brightness photo.jpg --factor 1.3 brighter.png
              sharpimage brightness photo.jpg --factor 0.7 darker.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(factorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double factor = parseResult.GetValue(factorOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Brightness ×{factor:F2}",
                img => ColorAdjust.Brightness(img, factor));
        });
        return cmd;
    }

    public static Command CreateContrastCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var factorOpt = new Option<double>("-f", "--factor") { Description = "Contrast factor (0.0=gray, 1.0=unchanged, 2.0=strong)", DefaultValueFactory = _ => 1.5 };
        var cmd = new Command("contrast", """
            Adjust image contrast.

            Examples:
              sharpimage contrast photo.jpg --factor 1.5 highcontrast.png
              sharpimage contrast photo.jpg --factor 0.5 lowcontrast.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(factorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double factor = parseResult.GetValue(factorOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Contrast ×{factor:F2}",
                img => ColorAdjust.Contrast(img, factor));
        });
        return cmd;
    }

    public static Command CreateGammaCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var valueOpt = new Option<double>("-v", "--value") { Description = "Gamma value (>1 brightens midtones, <1 darkens midtones)", DefaultValueFactory = _ => 2.2 };
        var cmd = new Command("gamma", """
            Apply gamma correction to an image.
            Standard monitor gamma is 2.2. Values >1 brighten midtones.

            Examples:
              sharpimage gamma photo.jpg --value 2.2 corrected.png
              sharpimage gamma dark.jpg --value 0.5 brightened.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(valueOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double val = parseResult.GetValue(valueOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Gamma correction (γ={val:F2})",
                img => ColorAdjust.Gamma(img, val));
        });
        return cmd;
    }

    public static Command CreateGrayscaleCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("grayscale", """
            Convert an image to grayscale using BT.709 luminance weights.
            Formula: 0.2126R + 0.7152G + 0.0722B

            Examples:
              sharpimage grayscale photo.jpg gray.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Converting to grayscale", ColorAdjust.Grayscale);
        });
        return cmd;
    }

    public static Command CreateInvertCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("invert", """
            Invert (negate) all colors in an image.
            Each pixel value becomes (max - value).

            Examples:
              sharpimage invert photo.jpg negative.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Inverting colors", ColorAdjust.Invert);
        });
        return cmd;
    }

    public static Command CreateThresholdCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var valueOpt = new Option<double>("-v", "--value") { Description = "Threshold value 0.0-1.0 (omit for auto Otsu)" };
        var adaptiveOpt = new Option<bool>("--adaptive") { Description = "Use adaptive (local) thresholding" };
        var windowOpt = new Option<int>("--window") { Description = "Adaptive threshold window size", DefaultValueFactory = _ => 15 };
        var offsetOpt = new Option<double>("--offset") { Description = "Adaptive threshold offset", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("threshold", """
            Apply thresholding to create a binary image.
            Without --value, uses Otsu's automatic threshold.
            With --adaptive, uses local adaptive thresholding.

            Examples:
              sharpimage threshold scan.png binary.png                     # Otsu auto
              sharpimage threshold scan.png --value 0.5 binary.png         # Manual
              sharpimage threshold doc.png --adaptive --window 25 clean.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(valueOpt);
        cmd.Add(adaptiveOpt);
        cmd.Add(windowOpt);
        cmd.Add(offsetOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            bool adaptive = parseResult.GetValue(adaptiveOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            if (adaptive)
            {
                int window = parseResult.GetValue(windowOpt);
                double offset = parseResult.GetValue(offsetOpt);
                CliOutput.RunPipeline(input, output, $"Adaptive threshold (window={window})",
                    img => SharpImage.Threshold.ThresholdOps.AdaptiveThreshold(img, window, offset));
            }
            else
            {
                double val = parseResult.GetValue(valueOpt);
                if (val == 0.0 && parseResult.GetResult(valueOpt) == null)
                {
                    CliOutput.RunMutatingPipeline(input, output, "Otsu auto-threshold", img =>
                    {
                        double t = SharpImage.Threshold.ThresholdOps.ApplyOtsuThreshold(img);
                        Spectre.Console.AnsiConsole.MarkupLine($"  [dim]Otsu threshold: {t:F4}[/]");
                    });
                }
                else
                {
                    CliOutput.RunMutatingPipeline(input, output, $"Threshold ({val:F2})", img =>
                        SharpImage.Threshold.ThresholdOps.BinaryThreshold(img, val));
                }
            }
        });
        return cmd;
    }

    public static Command CreatePosterizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var levelsOpt = new Option<int>("-l", "--levels") { Description = "Number of color levels per channel (2-256)", DefaultValueFactory = _ => 4 };
        var cmd = new Command("posterize", """
            Reduce the number of color levels in an image, creating a poster-like effect.

            Examples:
              sharpimage posterize photo.jpg --levels 4 poster.png
              sharpimage posterize photo.jpg --levels 2 binary-ish.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int levels = parseResult.GetValue(levelsOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Posterize ({levels} levels)",
                img => ColorAdjust.Posterize(img, levels));
        });
        return cmd;
    }

    public static Command CreateSaturateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var factorOpt = new Option<double>("-f", "--factor") { Description = "Saturation factor (0=grayscale, 1=unchanged, >1=oversaturated)", DefaultValueFactory = _ => 1.5 };
        var cmd = new Command("saturate", """
            Adjust color saturation.

            Examples:
              sharpimage saturate photo.jpg --factor 1.5 vivid.png
              sharpimage saturate photo.jpg --factor 0.0 desaturated.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(factorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double factor = parseResult.GetValue(factorOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Saturation ×{factor:F2}",
                img => ColorAdjust.Saturate(img, factor));
        });
        return cmd;
    }

    public static Command CreateLevelsCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var inBlackOpt = new Option<double>("--in-black") { Description = "Input black point (0.0-1.0)", DefaultValueFactory = _ => 0.0 };
        var inWhiteOpt = new Option<double>("--in-white") { Description = "Input white point (0.0-1.0)", DefaultValueFactory = _ => 1.0 };
        var outBlackOpt = new Option<double>("--out-black") { Description = "Output black point (0.0-1.0)", DefaultValueFactory = _ => 0.0 };
        var outWhiteOpt = new Option<double>("--out-white") { Description = "Output white point (0.0-1.0)", DefaultValueFactory = _ => 1.0 };
        var midGammaOpt = new Option<double>("--mid-gamma") { Description = "Midtone gamma (1.0=linear)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("levels", """
            Adjust input/output levels and midtone gamma.
            Similar to Photoshop's Levels dialog.

            Examples:
              sharpimage levels photo.jpg --in-black 0.1 --in-white 0.9 adjusted.png
              sharpimage levels photo.jpg --mid-gamma 1.5 brighter-mids.png
              sharpimage levels photo.jpg --out-black 0.2 --out-white 0.8 reduced-range.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(inBlackOpt);
        cmd.Add(inWhiteOpt);
        cmd.Add(outBlackOpt);
        cmd.Add(outWhiteOpt);
        cmd.Add(midGammaOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double inB = parseResult.GetValue(inBlackOpt);
            double inW = parseResult.GetValue(inWhiteOpt);
            double outB = parseResult.GetValue(outBlackOpt);
            double outW = parseResult.GetValue(outWhiteOpt);
            double mid = parseResult.GetValue(midGammaOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Levels adjustment",
                img => ColorAdjust.Levels(img, inB, inW, outB, outW, mid));
        });
        return cmd;
    }

    #endregion

    public static Command CreateColorMatrixCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var matrixArg = new Argument<string>("matrix") { Description = "Matrix values as comma-separated row-major floats. Size must be 3×3, 4×4, or 5×5." };
        var cmd = new Command("colormatrix", """
            Apply a color matrix transformation to each pixel.
            The matrix multiplies channel values (RGBA) to produce new values.
            
            Matrix format: comma-separated values, row by row.
            3×3 (9 values): R,G,B channels
            4×4 (16 values): RGBA channels
            5×5 (25 values): RGBA + offset column

            Presets: use --preset for common transforms.
            
            Examples:
              sharpimage colormatrix photo.jpg sepia.png --preset sepia
              sharpimage colormatrix photo.jpg swapped.png "0,0,1,0,1,0,1,0,0"
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        var presetOpt = new Option<string>("-p", "--preset") { Description = "Preset: sepia, grayscale, swap-rb, saturate, desaturate" };
        cmd.Add(matrixArg);
        cmd.Add(presetOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string? preset = parseResult.GetValue(presetOpt);
            string matrixStr = parseResult.GetValue(matrixArg)!;

            double[,]? matrix = null;

            if (!string.IsNullOrEmpty(preset))
            {
                matrix = preset.ToLowerInvariant() switch
                {
                    "sepia" => new double[,] {
                        { 0.393, 0.769, 0.189 },
                        { 0.349, 0.686, 0.168 },
                        { 0.272, 0.534, 0.131 }
                    },
                    "grayscale" => new double[,] {
                        { 0.2126, 0.7152, 0.0722 },
                        { 0.2126, 0.7152, 0.0722 },
                        { 0.2126, 0.7152, 0.0722 }
                    },
                    "swap-rb" => new double[,] {
                        { 0, 0, 1 },
                        { 0, 1, 0 },
                        { 1, 0, 0 }
                    },
                    "saturate" => new double[,] {
                        { 1.5, -0.25, -0.25 },
                        { -0.25, 1.5, -0.25 },
                        { -0.25, -0.25, 1.5 }
                    },
                    "desaturate" => new double[,] {
                        { 0.5, 0.25, 0.25 },
                        { 0.25, 0.5, 0.25 },
                        { 0.25, 0.25, 0.5 }
                    },
                    _ => null
                };
            }

            if (matrix is null && !string.IsNullOrEmpty(matrixStr))
            {
                var values = matrixStr.Split(',').Select(s => double.Parse(s.Trim())).ToArray();
                int size = (int)Math.Sqrt(values.Length);
                if (size * size != values.Length || size < 3 || size > 5)
                {
                    Console.Error.WriteLine($"Error: Matrix must have 9, 16, or 25 values (3×3, 4×4, 5×5). Got {values.Length}.");
                    return;
                }
                matrix = new double[size, size];
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                        matrix[i, j] = values[i * size + j];
            }

            if (matrix is null)
            {
                Console.Error.WriteLine("Error: Provide --preset or matrix values.");
                return;
            }

            if (!CliOutput.ValidateInputExists(input)) return;
            string label = !string.IsNullOrEmpty(preset) ? $"Color matrix ({preset})" : "Color matrix";
            CliOutput.RunPipeline(input, output, label,
                img => ColorAdjust.ColorMatrix(img, matrix));
        });
        return cmd;
    }

    // ─── Decorative & Paint Commands ─────────────────────────────

    public static Command CreateBorderCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "Border width in pixels", DefaultValueFactory = _ => 10 };
        var heightOpt = new Option<int>("-h", "--height") { Description = "Border height in pixels", DefaultValueFactory = _ => 10 };
        var colorOpt = new Option<string>("-c", "--color") { Description = "Border color R,G,B (0-65535)", DefaultValueFactory = _ => "0,0,0" };

        var cmd = new Command("border", "Add a solid-color border around the image.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(widthOpt); cmd.Add(heightOpt); cmd.Add(colorOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            var rgb = ParseColor(parseResult.GetValue(colorOpt)!);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Border {w}×{h}",
                img => DecorateOps.Border(img, w, h, rgb.r, rgb.g, rgb.b));
        });
        return cmd;
    }

    public static Command CreateFrameCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var matteWOpt = new Option<int>("--matte-width") { Description = "Matte border width", DefaultValueFactory = _ => 5 };
        var matteHOpt = new Option<int>("--matte-height") { Description = "Matte border height", DefaultValueFactory = _ => 5 };
        var outerOpt = new Option<int>("--outer-bevel") { Description = "Outer bevel size", DefaultValueFactory = _ => 4 };
        var innerOpt = new Option<int>("--inner-bevel") { Description = "Inner bevel size", DefaultValueFactory = _ => 2 };

        var cmd = new Command("frame", "Add a 3D beveled frame around the image.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(matteWOpt); cmd.Add(matteHOpt); cmd.Add(outerOpt); cmd.Add(innerOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int mw = parseResult.GetValue(matteWOpt);
            int mh = parseResult.GetValue(matteHOpt);
            int ob = parseResult.GetValue(outerOpt);
            int ib = parseResult.GetValue(innerOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Frame {ob}/{ib} bevel",
                img => DecorateOps.Frame(img, mw, mh, ob, ib));
        });
        return cmd;
    }

    public static Command CreateRaiseCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "Raise edge width", DefaultValueFactory = _ => 6 };
        var heightOpt = new Option<int>("-h", "--height") { Description = "Raise edge height", DefaultValueFactory = _ => 6 };
        var sunkenOpt = new Option<bool>("--sunken") { Description = "Create sunken effect instead of raised" };

        var cmd = new Command("raise", "Apply a 3D raised or sunken button effect to image edges.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(widthOpt); cmd.Add(heightOpt); cmd.Add(sunkenOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            bool sunken = parseResult.GetValue(sunkenOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, sunken ? "Sunken edges" : "Raised edges",
                img => DecorateOps.Raise(img, w, h, raise: !sunken));
        });
        return cmd;
    }

    public static Command CreateShadeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var azimuthOpt = new Option<double>("--azimuth") { Description = "Light azimuth in degrees (0=right)", DefaultValueFactory = _ => 315.0 };
        var elevationOpt = new Option<double>("--elevation") { Description = "Light elevation in degrees (0=horizon, 90=above)", DefaultValueFactory = _ => 45.0 };
        var grayOpt = new Option<bool>("--gray") { Description = "Modulate intensity instead of outputting shade" };

        var cmd = new Command("shade", "Apply directional light shading effect.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(azimuthOpt); cmd.Add(elevationOpt); cmd.Add(grayOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double az = parseResult.GetValue(azimuthOpt);
            double el = parseResult.GetValue(elevationOpt);
            bool gray = parseResult.GetValue(grayOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Shade az={az:F0}° el={el:F0}°",
                img => DecorateOps.Shade(img, gray, az, el));
        });
        return cmd;
    }

    public static Command CreateOpaquePaintCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var targetOpt = new Option<string>("--target") { Description = "Target color R,G,B (0-65535)" };
        var fillOpt = new Option<string>("--fill") { Description = "Fill color R,G,B (0-65535)" };
        var fuzzOpt = new Option<double>("--fuzz") { Description = "Color distance tolerance", DefaultValueFactory = _ => 0.0 };
        var invertOpt = new Option<bool>("--invert") { Description = "Replace non-matching pixels instead" };

        targetOpt.Required = true;
        fillOpt.Required = true;

        var cmd = new Command("opaquepaint", "Replace all pixels matching a target color with a fill color.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(targetOpt); cmd.Add(fillOpt); cmd.Add(fuzzOpt); cmd.Add(invertOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var target = ParseColor(parseResult.GetValue(targetOpt)!);
            var fill = ParseColor(parseResult.GetValue(fillOpt)!);
            double fuzz = parseResult.GetValue(fuzzOpt);
            bool invert = parseResult.GetValue(invertOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Opaque paint",
                img => DecorateOps.OpaquePaint(img, target.r, target.g, target.b,
                    fill.r, fill.g, fill.b, fuzz, invert));
        });
        return cmd;
    }

    public static Command CreateTransparentPaintCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var targetOpt = new Option<string>("--target") { Description = "Target color R,G,B (0-65535)" };
        var fuzzOpt = new Option<double>("--fuzz") { Description = "Color distance tolerance", DefaultValueFactory = _ => 0.0 };

        targetOpt.Required = true;

        var cmd = new Command("transparentpaint", "Make pixels matching a target color transparent.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(targetOpt); cmd.Add(fuzzOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var target = ParseColor(parseResult.GetValue(targetOpt)!);
            double fuzz = parseResult.GetValue(fuzzOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Transparent paint",
                img => DecorateOps.TransparentPaint(img, target.r, target.g, target.b, fuzz));
        });
        return cmd;
    }

    public static Command CreateShearCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<double>("--x-shear") { Description = "Horizontal shear angle in degrees", DefaultValueFactory = _ => 0.0 };
        var yOpt = new Option<double>("--y-shear") { Description = "Vertical shear angle in degrees", DefaultValueFactory = _ => 0.0 };

        var cmd = new Command("shear", "Apply a shear (skew) transformation.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(xOpt); cmd.Add(yOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double x = parseResult.GetValue(xOpt);
            double y = parseResult.GetValue(yOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Shear {x:F1}°/{y:F1}°",
                img => SharpImage.Transform.Geometry.Shear(img, x, y));
        });
        return cmd;
    }

    // ─── Bundle G Commands ─────────────────────────────────────────

    public static Command CreateAutoOrientCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("autoorient", "Apply EXIF orientation tag and reset to TopLeft.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "AutoOrient",
                img => SharpImage.Transform.Geometry.AutoOrient(img));
        });
        return cmd;
    }

    public static Command CreateRollCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<int>("--x") { Description = "Horizontal roll offset", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("--y") { Description = "Vertical roll offset", DefaultValueFactory = _ => 0 };
        var cmd = new Command("roll", "Circular pixel shift (wraps around edges).");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(xOpt); cmd.Add(yOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Roll {x},{y}",
                img => SharpImage.Transform.Geometry.Roll(img, x, y));
        });
        return cmd;
    }

    public static Command CreateSpliceCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<int>("--x") { Description = "Insert position X", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("--y") { Description = "Insert position Y", DefaultValueFactory = _ => 0 };
        var wOpt = new Option<int>("--width") { Description = "Width of inserted space", DefaultValueFactory = _ => 10 };
        var hOpt = new Option<int>("--height") { Description = "Height of inserted space", DefaultValueFactory = _ => 10 };
        var colorOpt = new Option<string>("--color") { Description = "Fill color as R,G,B (quantum)", DefaultValueFactory = _ => "0,0,0" };
        var cmd = new Command("splice", "Insert blank space into an image (inverse of chop).");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(xOpt); cmd.Add(yOpt);
        cmd.Add(wOpt); cmd.Add(hOpt); cmd.Add(colorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            int w = parseResult.GetValue(wOpt);
            int h = parseResult.GetValue(hOpt);
            var (r, g, b) = ParseColor(parseResult.GetValue(colorOpt)!);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Splice {w}x{h} at {x},{y}",
                img => SharpImage.Transform.Geometry.Splice(img, x, y, w, h, r, g, b));
        });
        return cmd;
    }

    public static Command CreateBlueShiftCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var factorOpt = new Option<double>("--factor") { Description = "Blue shift factor (default 1.5)", DefaultValueFactory = _ => 1.5 };
        var cmd = new Command("blueshift", "Simulate moonlight blue-shift effect.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(factorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double factor = parseResult.GetValue(factorOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"BlueShift {factor:F1}",
                img => VisualEffectsOps.BlueShift(img, factor));
        });
        return cmd;
    }

    public static Command CreateTintCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var colorOpt = new Option<string>("--color") { Description = "Tint color as R,G,B (quantum)", DefaultValueFactory = _ => "65535,0,0" };
        var blendOpt = new Option<double>("--blend") { Description = "Blend percentage (0-100)", DefaultValueFactory = _ => 100.0 };
        var cmd = new Command("tint", "Apply color tint weighted toward midtones.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(colorOpt); cmd.Add(blendOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var (r, g, b) = ParseColor(parseResult.GetValue(colorOpt)!);
            double blend = parseResult.GetValue(blendOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Tint",
                img => VisualEffectsOps.Tint(img, r, g, b, blend, blend, blend));
        });
        return cmd;
    }

    public static Command CreateShadowCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var alphaOpt = new Option<double>("--alpha") { Description = "Shadow opacity (0-100)", DefaultValueFactory = _ => 80.0 };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Blur sigma", DefaultValueFactory = _ => 4.0 };
        var xOpt = new Option<int>("--x") { Description = "X offset", DefaultValueFactory = _ => 4 };
        var yOpt = new Option<int>("--y") { Description = "Y offset", DefaultValueFactory = _ => 4 };
        var cmd = new Command("shadow", "Generate a drop shadow of the image.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(alphaOpt); cmd.Add(sigmaOpt); cmd.Add(xOpt); cmd.Add(yOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double alpha = parseResult.GetValue(alphaOpt);
            double sigma = parseResult.GetValue(sigmaOpt);
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Shadow",
                img => VisualEffectsOps.Shadow(img, alpha, sigma, x, y));
        });
        return cmd;
    }

    public static Command CreateSteganoCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Cover image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var wmArg = new Option<string>("--watermark") { Description = "Watermark image to embed", Required = true };
        var depthOpt = new Option<int>("--depth") { Description = "Bit depth for embedding (1-8)", DefaultValueFactory = _ => 1 };
        var cmd = new Command("stegano", "Embed a watermark image in LSBs of cover image.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(wmArg); cmd.Add(depthOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string watermarkPath = parseResult.GetValue(wmArg)!;
            int depth = parseResult.GetValue(depthOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(watermarkPath)) return;

            var cover = SharpImage.Formats.FormatRegistry.Read(input);
            var watermark = SharpImage.Formats.FormatRegistry.Read(watermarkPath);
            var result = VisualEffectsOps.SteganoEmbed(cover, watermark, depth);
            CliOutput.WriteImageWithProgress(result, output);
            cover.Dispose();
            watermark.Dispose();
            result.Dispose();
        });
        return cmd;
    }

    public static Command CreatePolaroidCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var angleOpt = new Option<double>("--angle") { Description = "Rotation angle in degrees", DefaultValueFactory = _ => 0.0 };
        var borderOpt = new Option<int>("--border") { Description = "Border size in pixels", DefaultValueFactory = _ => 20 };
        var cmd = new Command("polaroid", "Simulate a Polaroid photo with border and optional rotation.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(angleOpt); cmd.Add(borderOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double angle = parseResult.GetValue(angleOpt);
            int border = parseResult.GetValue(borderOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Polaroid",
                img => VisualEffectsOps.Polaroid(img, angle, border));
        });
        return cmd;
    }

    public static Command CreateSegmentCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var clusterOpt = new Option<double>("--cluster") { Description = "Cluster threshold", DefaultValueFactory = _ => 1.0 };
        var smoothOpt = new Option<double>("--smooth") { Description = "Smoothing threshold", DefaultValueFactory = _ => 1.5 };
        var cmd = new Command("segment", "Segment image into distinct color regions.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(clusterOpt); cmd.Add(smoothOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double cluster = parseResult.GetValue(clusterOpt);
            double smooth = parseResult.GetValue(smoothOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Segment",
                img => VisualEffectsOps.Segment(img, cluster, smooth));
        });
        return cmd;
    }

    public static Command CreateSparseColorCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var methodOpt = new Option<string>("--method") { Description = "Interpolation method (voronoi|shepards|manhattan|inverse)", DefaultValueFactory = _ => "shepards" };
        var pointsOpt = new Option<string>("--points") { Description = "Control points: x1,y1,r1,g1,b1;x2,y2,r2,g2,b2;...", Required = true };
        var cmd = new Command("sparsecolor", "Interpolate colors at sparse control points.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(methodOpt); cmd.Add(pointsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string methodStr = parseResult.GetValue(methodOpt)!;
            string pointsStr = parseResult.GetValue(pointsOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            var method = methodStr.ToLowerInvariant() switch
            {
                "voronoi" => SparseColorMethod.Voronoi,
                "manhattan" => SparseColorMethod.Manhattan,
                "inverse" => SparseColorMethod.InverseDistance,
                _ => SparseColorMethod.Shepards,
            };

            var points = pointsStr.Split(';')
                .Select(p =>
                {
                    var vals = p.Split(',').Select(v => double.Parse(v.Trim())).ToArray();
                    return new SparseColorPoint(vals[0], vals[1], vals[2], vals[3], vals[4]);
                })
                .ToArray();

            CliOutput.RunPipeline(input, output, $"SparseColor ({methodStr})",
                img => VisualEffectsOps.SparseColor(img, method, points));
        });
        return cmd;
    }

    internal static (ushort r, ushort g, ushort b) ParseColor(string rgb)
    {
        var parts = rgb.Split(',');
        if (parts.Length != 3)
            throw new ArgumentException("Color must be in R,G,B format (e.g., 65535,0,0)");
        return (ushort.Parse(parts[0].Trim()), ushort.Parse(parts[1].Trim()), ushort.Parse(parts[2].Trim()));
    }

    // ─── Bundle K: HDR & Multi-Exposure CLI commands ────────────────

    public static Command CreateHdrMergeCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output HDR image path" };
        var inputsOpt = new Option<string[]>("--inputs") { Description = "Input exposure image paths", Required = true };
        inputsOpt.AllowMultipleArgumentsPerToken = true;
        var evsOpt = new Option<string>("--evs") { Description = "Comma-separated EV values matching inputs", Required = true };
        var cmd = new Command("hdrmerge", "Merge multiple exposures into an HDR image.");
        cmd.Add(outputArg); cmd.Add(inputsOpt); cmd.Add(evsOpt);
        cmd.SetAction((parseResult) =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string[] inputs = parseResult.GetValue(inputsOpt)!;
            double[] evs = parseResult.GetValue(evsOpt)!.Split(',').Select(double.Parse).ToArray();
            foreach (var inp in inputs) if (!CliOutput.ValidateInputExists(inp)) return;

            var images = inputs.Select(p => SharpImage.Formats.FormatRegistry.Read(p)).ToArray();
            var result = HdrOps.HdrMerge(images, evs);
            CliOutput.WriteImageWithProgress(result, output);
            foreach (var img in images) img.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateToneMapReinhardCommand()
    {
        var cmd = new Command("tonemap-reinhard", "Apply Reinhard tone mapping.");
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var keyOpt = new Option<double>("--key") { Description = "Exposure key (0.09-0.36)", DefaultValueFactory = _ => 0.18 };
        var satOpt = new Option<double>("--saturation") { Description = "Saturation (0-1)", DefaultValueFactory = _ => 1.0 };
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(keyOpt); cmd.Add(satOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double key = parseResult.GetValue(keyOpt);
            double sat = parseResult.GetValue(satOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Tone map (Reinhard)",
                img => HdrOps.ToneMapReinhard(img, key, sat));
        });
        return cmd;
    }

    public static Command CreateToneMapDragoCommand()
    {
        var cmd = new Command("tonemap-drago", "Apply Drago logarithmic tone mapping.");
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var biasOpt = new Option<double>("--bias") { Description = "Contrast bias (0.7-0.9)", DefaultValueFactory = _ => 0.85 };
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(biasOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double bias = parseResult.GetValue(biasOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Tone map (Drago)",
                img => HdrOps.ToneMapDrago(img, bias));
        });
        return cmd;
    }

    public static Command CreateExposureFusionCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output fused image path" };
        var inputsOpt = new Option<string[]>("--inputs") { Description = "Input exposure image paths", Required = true };
        inputsOpt.AllowMultipleArgumentsPerToken = true;
        var cmd = new Command("expfusion", "Fuse multiple exposures directly (Mertens algorithm).");
        cmd.Add(outputArg); cmd.Add(inputsOpt);
        cmd.SetAction((parseResult) =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string[] inputs = parseResult.GetValue(inputsOpt)!;
            foreach (var inp in inputs) if (!CliOutput.ValidateInputExists(inp)) return;

            var images = inputs.Select(p => SharpImage.Formats.FormatRegistry.Read(p)).ToArray();
            var result = HdrOps.ExposureFusion(images);
            CliOutput.WriteImageWithProgress(result, output);
            foreach (var img in images) img.Dispose(); result.Dispose();
        });
        return cmd;
    }

    // ─── Bundle L: Creative Filters II ──────────────────────────

    public static Command CreateLensBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Blur radius", DefaultValueFactory = _ => 5 };
        var shapeOpt = new Option<string>("--shape") { Description = "Bokeh shape: disk or hexagon", DefaultValueFactory = _ => "disk" };
        var depthOpt = new Option<string?>("--depth") { Description = "Optional depth map image (white=blurry)" };
        var cmd = new Command("lensblur", "Simulate camera lens blur (bokeh) with optional depth map.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(radiusOpt); cmd.Add(shapeOpt); cmd.Add(depthOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var radius = parseResult.GetValue(radiusOpt);
            var shapeName = parseResult.GetValue(shapeOpt)!;
            var depthPath = parseResult.GetValue(depthOpt);
            var shape = shapeName.Equals("hexagon", StringComparison.OrdinalIgnoreCase) ? BokehShape.Hexagon : BokehShape.Disk;
            CliOutput.RunPipeline(input, output, "Lens Blur", img =>
            {
                ImageFrame? depth = depthPath != null ? SharpImage.Formats.FormatRegistry.Read(depthPath) : null;
                var result = CreativeFilters.LensBlur(img, radius, shape, depth);
                depth?.Dispose();
                return result;
            });
        });
        return cmd;
    }

    public static Command CreateTiltShiftCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var focusOpt = new Option<double>("--focus") { Description = "Focus band center (0.0-1.0)", DefaultValueFactory = _ => 0.5 };
        var bandOpt = new Option<double>("--band") { Description = "Focus band height (0.0-1.0)", DefaultValueFactory = _ => 0.2 };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Max blur radius", DefaultValueFactory = _ => 8 };
        var cmd = new Command("tiltshift", "Simulate tilt-shift miniature photography.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(focusOpt); cmd.Add(bandOpt); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Tilt-Shift", img =>
                CreativeFilters.TiltShift(img, parseResult.GetValue(focusOpt), parseResult.GetValue(bandOpt), parseResult.GetValue(radiusOpt)));
        });
        return cmd;
    }

    public static Command CreateGlowCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var threshOpt = new Option<double>("--threshold") { Description = "Brightness threshold (0.0-1.0)", DefaultValueFactory = _ => 0.5 };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Glow blur radius", DefaultValueFactory = _ => 10 };
        var intensityOpt = new Option<double>("--intensity") { Description = "Glow intensity (0.0-1.0)", DefaultValueFactory = _ => 0.6 };
        var cmd = new Command("glow", "Add soft glow/bloom effect to bright areas.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(threshOpt); cmd.Add(radiusOpt); cmd.Add(intensityOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Glow", img =>
                CreativeFilters.Glow(img, parseResult.GetValue(threshOpt), parseResult.GetValue(radiusOpt), parseResult.GetValue(intensityOpt)));
        });
        return cmd;
    }

    public static Command CreatePixelateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sizeOpt = new Option<int>("--size") { Description = "Block size in pixels", DefaultValueFactory = _ => 8 };
        var cmd = new Command("pixelate", "Pixelate an image into solid-colored blocks.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(sizeOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Pixelate", img =>
                CreativeFilters.Pixelate(img, parseResult.GetValue(sizeOpt)));
        });
        return cmd;
    }

    public static Command CreateCrystallizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cellOpt = new Option<int>("--cell") { Description = "Cell size", DefaultValueFactory = _ => 16 };
        var seedOpt = new Option<int>("--seed") { Description = "Random seed", DefaultValueFactory = _ => 42 };
        var cmd = new Command("crystallize", "Create Voronoi cell-based pixelation effect.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(cellOpt); cmd.Add(seedOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Crystallize", img =>
                CreativeFilters.Crystallize(img, parseResult.GetValue(cellOpt), parseResult.GetValue(seedOpt)));
        });
        return cmd;
    }

    public static Command CreatePointillizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var dotOpt = new Option<int>("--dot") { Description = "Dot radius", DefaultValueFactory = _ => 4 };
        var spacingOpt = new Option<int>("--spacing") { Description = "Dot spacing (0=auto)", DefaultValueFactory = _ => 0 };
        var cmd = new Command("pointillize", "Create Seurat-style pointillist painting effect.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(dotOpt); cmd.Add(spacingOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Pointillize", img =>
                CreativeFilters.Pointillize(img, parseResult.GetValue(dotOpt), parseResult.GetValue(spacingOpt)));
        });
        return cmd;
    }

    public static Command CreateHalftoneCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var dotOpt = new Option<int>("--dot") { Description = "Halftone dot size", DefaultValueFactory = _ => 6 };
        var cmd = new Command("halftone", "Simulate CMYK halftone print screening.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(dotOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Halftone", img =>
                CreativeFilters.Halftone(img, parseResult.GetValue(dotOpt)));
        });
        return cmd;
    }

    public static Command CreateMorphCommand()
    {
        var input1Arg = new Argument<string>("input1") { Description = "First input image" };
        var input2Arg = new Argument<string>("input2") { Description = "Second input image" };
        var outputArg = new Argument<string>("output-prefix") { Description = "Output filename prefix (frames saved as prefix_001.png, etc.)" };
        var framesOpt = new Option<int>("-n", "--frames") { Description = "Number of intermediate frames", DefaultValueFactory = _ => 5 };
        var cmd = new Command("morph", """
            Create cross-dissolve morphing frames between two images.
            Outputs (N+2) frames: source, N intermediates, target.

            Examples:
              sharpimage morph start.png end.png morph_ -n 10
            """);
        cmd.Add(input1Arg);
        cmd.Add(input2Arg);
        cmd.Add(outputArg);
        cmd.Add(framesOpt);
        cmd.SetAction((parseResult) =>
        {
            string in1 = parseResult.GetValue(input1Arg)!;
            string in2 = parseResult.GetValue(input2Arg)!;
            string prefix = parseResult.GetValue(outputArg)!;
            int frames = parseResult.GetValue(framesOpt);
            if (!CliOutput.ValidateInputExists(in1) || !CliOutput.ValidateInputExists(in2)) return;

            using var img1 = SharpImage.Formats.FormatRegistry.Read(in1);
            using var img2 = SharpImage.Formats.FormatRegistry.Read(in2);
            var morphFrames = MorphOps.Morph(img1, img2, frames);
            for (int i = 0; i < morphFrames.Length; i++)
            {
                string outPath = $"{prefix}{i + 1:D3}.png";
                SharpImage.Formats.FormatRegistry.Write(morphFrames[i], outPath);
                morphFrames[i].Dispose();
            }
            CliOutput.PrintSuccess(in1, $"{prefix}*.png ({morphFrames.Length} frames)", TimeSpan.Zero);
        });
        return cmd;
    }

    public static Command CreateAnaglyphCommand()
    {
        var leftArg = new Argument<string>("left") { Description = "Left eye image" };
        var rightArg = new Argument<string>("right") { Description = "Right eye image" };
        var outputArg = new Argument<string>("output") { Description = "Output anaglyph image" };
        var cmd = new Command("anaglyph", """
            Create a red/cyan stereo anaglyph from a left/right image pair.
            View with red/cyan 3D glasses.

            Examples:
              sharpimage anaglyph left.png right.png stereo.png
            """);
        cmd.Add(leftArg);
        cmd.Add(rightArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string left = parseResult.GetValue(leftArg)!;
            string right = parseResult.GetValue(rightArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(left) || !CliOutput.ValidateInputExists(right)) return;

            using var imgL = SharpImage.Formats.FormatRegistry.Read(left);
            using var imgR = SharpImage.Formats.FormatRegistry.Read(right);
            using var result = StereoOps.Anaglyph(imgL, imgR);
            SharpImage.Formats.FormatRegistry.Write(result, output);
            CliOutput.PrintSuccess(left, output, TimeSpan.Zero);
        });
        return cmd;
    }

    public static Command CreateEnhanceCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("enhance", """
            Noise reduction using edge-preserving weighted neighbor averaging.
            Automatically detects and preserves edges while smoothing noise.

            Examples:
              sharpimage enhance noisy.png clean.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, "Enhance (noise reduction)",
                img => NoiseReduceOps.Enhance(img));
        });
        return cmd;
    }

    public static Command CreateChromaKeyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path (PNG for transparency)" };
        var colorOpt = new Option<string>("-c", "--color") { Description = "Key color as hex (e.g. 00FF00 for green)", DefaultValueFactory = _ => "00FF00" };
        var tolOpt = new Option<double>("-t", "--tolerance") { Description = "Color distance tolerance (0–1)", DefaultValueFactory = _ => 0.15 };
        var cmd = new Command("chromakey", """
            Remove a specific background color (green screen, blue screen, etc.).
            Replaces the key color with transparency.

            Examples:
              sharpimage chromakey greenscreen.png result.png -c 00FF00 -t 0.2
              sharpimage chromakey bluescreen.jpg result.png -c 0000FF
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(colorOpt);
        cmd.Add(tolOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string colorHex = parseResult.GetValue(colorOpt)!;
            double tol = parseResult.GetValue(tolOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            ParseHexColor(colorHex, out ushort keyR, out ushort keyG, out ushort keyB);

            CliOutput.RunPipeline(input, output, $"Chroma Key (#{colorHex}, tol={tol:F2})",
                img => ChromaKeyOps.ChromaKey(img, keyR, keyG, keyB, tol));
        });
        return cmd;
    }

    public static Command CreateChannelFxCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var exprOpt = new Option<string>("-e", "--expression") { Description = "Channel expression (e.g. 'red=>blue' or 'swap:red,blue')" };
        var cmd = new Command("channelfx", """
            Apply channel manipulation expressions.
            Supports: copy (red=>blue), swap (swap:red,blue).
            Multiple operations separated by semicolons.

            Examples:
              sharpimage channelfx photo.jpg -e "red=>blue" swapped.png
              sharpimage channelfx photo.jpg -e "swap:red,green" result.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(exprOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string? expr = parseResult.GetValue(exprOpt);
            if (!CliOutput.ValidateInputExists(input) || string.IsNullOrWhiteSpace(expr)) return;

            CliOutput.RunPipeline(input, output, $"Channel FX ({expr})",
                img => ChannelFxOps.ChannelFx(img, expr));
        });
        return cmd;
    }

    public static Command CreateCdlCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var slopeOpt = new Option<string>("--slope") { Description = "Slope R,G,B (e.g. 1.1,1.0,0.9)", DefaultValueFactory = _ => "1,1,1" };
        var offsetOpt = new Option<string>("--offset") { Description = "Offset R,G,B", DefaultValueFactory = _ => "0,0,0" };
        var powerOpt = new Option<string>("--power") { Description = "Power (gamma) R,G,B", DefaultValueFactory = _ => "1,1,1" };
        var satOpt = new Option<double>("--saturation") { Description = "Saturation adjustment", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("cdl", """
            Apply ASC-CDL (Color Decision List) color correction.
            Formula: out = pow(in * slope + offset, power)
            Standard used in film and broadcast color grading.

            Examples:
              sharpimage cdl photo.jpg --slope 1.1,1.0,0.9 --offset 0.01,0,0 --power 1,1,1 graded.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(slopeOpt);
        cmd.Add(offsetOpt);
        cmd.Add(powerOpt);
        cmd.Add(satOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sat = parseResult.GetValue(satOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            var slope = ParseTriple(parseResult.GetValue(slopeOpt)!);
            var offset = ParseTriple(parseResult.GetValue(offsetOpt)!);
            var power = ParseTriple(parseResult.GetValue(powerOpt)!);

            CliOutput.RunPipeline(input, output, "CDL Color Correction",
                img => CdlOps.ApplyCdl(img,
                    slope.r, slope.g, slope.b,
                    offset.r, offset.g, offset.b,
                    power.r, power.g, power.b,
                    sat));
        });
        return cmd;
    }

    private static void ParseHexColor(string hex, out ushort r, out ushort g, out ushort b)
    {
        hex = hex.TrimStart('#');
        byte rb = Convert.ToByte(hex[..2], 16);
        byte gb = Convert.ToByte(hex[2..4], 16);
        byte bb = Convert.ToByte(hex[4..6], 16);
        r = SharpImage.Core.Quantum.ScaleFromByte(rb);
        g = SharpImage.Core.Quantum.ScaleFromByte(gb);
        b = SharpImage.Core.Quantum.ScaleFromByte(bb);
    }

    private static (double r, double g, double b) ParseTriple(string s)
    {
        var parts = s.Split(',');
        return (double.Parse(parts[0].Trim()), double.Parse(parts[1].Trim()), double.Parse(parts[2].Trim()));
    }

    public static Command CreateTextureImageCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Target image to fill" };
        var textureArg = new Argument<string>("texture") { Description = "Texture image to tile" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("textureimage", """
            Tile a texture/pattern across the entire target image by repeating the texture.

            Examples:
              sharpimage textureimage canvas.png brick.png result.png
            """);
        cmd.Add(inputArg); cmd.Add(textureArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string texture = parseResult.GetValue(textureArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input) || !CliOutput.ValidateInputExists(texture)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var target = CliOutput.ReadImageWithProgress(input);
            var tex = CliOutput.ReadImageWithProgress(texture);
            var result = UtilityOps.TextureImage(target, tex);
            CliOutput.WriteImageWithProgress(result, output);
            sw.Stop();
            target.Dispose(); tex.Dispose(); result.Dispose();
            CliOutput.PrintSuccess(input, output, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateRemapImageCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var refArg = new Argument<string>("reference") { Description = "Reference image for palette extraction" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var colorsOpt = new Option<int>("--colors") { Description = "Number of palette colors to extract from reference", DefaultValueFactory = _ => 256 };
        var ditherOpt = new Option<bool>("--dither") { Description = "Apply Floyd-Steinberg dithering", DefaultValueFactory = _ => false };
        var cmd = new Command("remap", """
            Remap image colors to match the palette of a reference image.
            Extracts dominant colors from the reference, then maps each pixel to the nearest match.

            Examples:
              sharpimage remap photo.png palette_ref.png remapped.png --colors 16
              sharpimage remap photo.png palette_ref.png remapped.png --dither
            """);
        cmd.Add(inputArg); cmd.Add(refArg); cmd.Add(outputArg);
        cmd.Options.Add(colorsOpt); cmd.Options.Add(ditherOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string reference = parseResult.GetValue(refArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int colors = parseResult.GetValue(colorsOpt);
            bool dither = parseResult.GetValue(ditherOpt);
            if (!CliOutput.ValidateInputExists(input) || !CliOutput.ValidateInputExists(reference)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var image = CliOutput.ReadImageWithProgress(input);
            var refImage = CliOutput.ReadImageWithProgress(reference);
            var result = UtilityOps.RemapImage(image, refImage, colors, dither);
            CliOutput.WriteImageWithProgress(result, output);
            sw.Stop();
            image.Dispose(); refImage.Dispose(); result.Dispose();
            CliOutput.PrintSuccess(input, output, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateRangeThresholdCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var hardLowOpt = new Option<double>("--hard-low") { Description = "Hard low boundary (0-1)", DefaultValueFactory = _ => 0.1 };
        var softLowOpt = new Option<double>("--soft-low") { Description = "Soft low boundary (0-1)", DefaultValueFactory = _ => 0.3 };
        var softHighOpt = new Option<double>("--soft-high") { Description = "Soft high boundary (0-1)", DefaultValueFactory = _ => 0.7 };
        var hardHighOpt = new Option<double>("--hard-high") { Description = "Hard high boundary (0-1)", DefaultValueFactory = _ => 0.9 };
        var cmd = new Command("rangethreshold", """
            Multi-level range threshold with soft inner/outer boundaries.
            Pixels in [softLow..softHigh] become white, outside [hardLow..hardHigh] become black,
            and pixels in the soft zones get a gradient transition.

            Examples:
              sharpimage rangethreshold photo.png result.png --hard-low 0.1 --soft-low 0.3 --soft-high 0.7 --hard-high 0.9
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Options.Add(hardLowOpt); cmd.Options.Add(softLowOpt);
        cmd.Options.Add(softHighOpt); cmd.Options.Add(hardHighOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double hl = parseResult.GetValue(hardLowOpt);
            double sl = parseResult.GetValue(softLowOpt);
            double sh = parseResult.GetValue(softHighOpt);
            double hh = parseResult.GetValue(hardHighOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, $"Range Threshold [{hl:F2}..{sl:F2}|{sh:F2}..{hh:F2}]",
                img =>
                {
                    SharpImage.Threshold.ThresholdOps.RangeThreshold(img, hl, sl, sh, hh);
                    return img;
                });
        });
        return cmd;
    }

    public static Command CreateDistanceTransformCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output distance map path" };
        var metricOpt = new Option<string>("--metric") { Description = "Distance metric: euclidean, manhattan, chebyshev", DefaultValueFactory = _ => "euclidean" };

        var cmd = new Command("distance-transform", """
            Compute a distance transform: each foreground pixel's value becomes its
            distance to the nearest background (dark) pixel. Result is normalized to full range.

            Examples:
              sharpimage distance-transform mask.png distance.png
              sharpimage distance-transform edges.png dist.png --metric manhattan
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(metricOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string metricStr = parseResult.GetValue(metricOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            var metric = metricStr.ToLowerInvariant() switch
            {
                "manhattan" => SharpImage.Morphology.DistanceMetric.Manhattan,
                "chebyshev" => SharpImage.Morphology.DistanceMetric.Chebyshev,
                _ => SharpImage.Morphology.DistanceMetric.Euclidean
            };

            CliOutput.RunPipeline(input, output, $"Distance transform ({metricStr})", img =>
                SharpImage.Morphology.MorphologyOps.DistanceTransform(img, metric));
        });
        return cmd;
    }

    // ─── Phase 49: Color Threshold ──────────────────────────────

    public static Command CreateColorThresholdCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var startOpt = new Option<string>("--start") { Description = "Start color as 'R,G,B' (0-1 each)", DefaultValueFactory = _ => "0,0,0" };
        var endOpt = new Option<string>("--end") { Description = "End color as 'R,G,B' (0-1 each)", DefaultValueFactory = _ => "0.5,0.5,0.5" };
        var cmd = new Command("colorthreshold", """
            Threshold by color range. Pixels inside the RGB range become white,
            pixels outside become black.

            Examples:
              sharpimage colorthreshold photo.png result.png --start "0.2,0,0" --end "1,0.3,0.3"
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(startOpt); cmd.Add(endOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var s = parseResult.GetValue(startOpt)!.Split(',').Select(double.Parse).ToArray();
            var e = parseResult.GetValue(endOpt)!.Split(',').Select(double.Parse).ToArray();
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, $"Color Threshold",
                img =>
                {
                    SharpImage.Threshold.ThresholdOps.ColorThreshold(img, s[0], s[1], s[2], e[0], e[1], e[2]);
                    return img;
                });
        });
        return cmd;
    }

    public static Command CreateRandomThresholdCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var lowOpt = new Option<double>("--low") { Description = "Low bound of random range (0-1)", DefaultValueFactory = _ => 0.0 };
        var highOpt = new Option<double>("--high") { Description = "High bound of random range (0-1)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("randomthreshold", """
            Stochastic random threshold dithering. Each pixel is compared against
            a random value in [low..high], producing a dithered effect.

            Examples:
              sharpimage randomthreshold photo.png dithered.png
              sharpimage randomthreshold photo.png dithered.png --low 0.3 --high 0.7
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(lowOpt); cmd.Add(highOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double low = parseResult.GetValue(lowOpt);
            double high = parseResult.GetValue(highOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, $"Random Threshold [{low:F2}..{high:F2}]",
                img =>
                {
                    SharpImage.Threshold.ThresholdOps.RandomThreshold(img, low, high);
                    return img;
                });
        });
        return cmd;
    }

    public static Command CreateSortPixelsCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("sortpixels", """
            Sort each row's pixels by luminance, creating an artistic glitch-style effect.
            Each scanline becomes a smooth gradient of its original colors.

            Examples:
              sharpimage sortpixels photo.png sorted.png
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, "Sort Pixels",
                img => SharpImage.Effects.UtilityOps.SortPixels(img));
        });
        return cmd;
    }

    public static Command CreateUniqueColorsCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output palette strip image path" };
        var countOnlyOpt = new Option<bool>("--count") { Description = "Only print the count, don't generate palette image" };
        var cmd = new Command("uniquecolors", """
            Analyze unique colors in an image. Can output just the count
            or generate a 1-pixel-tall palette strip image of all distinct colors.

            Examples:
              sharpimage uniquecolors photo.png palette.png
              sharpimage uniquecolors photo.png --count palette.png
            """);
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(countOnlyOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            bool countOnly = parseResult.GetValue(countOnlyOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            if (countOnly)
            {
                var img = CliOutput.ReadImageWithProgress(input);
                int count = SharpImage.Effects.UtilityOps.CountUniqueColors(img);
                Spectre.Console.AnsiConsole.MarkupLine($"[green]Unique colors:[/] {count}");
            }
            else
            {
                CliOutput.RunPipeline(input, output, "Unique Colors",
                    img => SharpImage.Effects.UtilityOps.UniqueColorsImage(img));
            }
        });
        return cmd;
    }

    public static Command CreateClampCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("clamp", """
            Force all pixel values into the valid quantum range.
            Useful after arithmetic operations that may produce out-of-range values.

            Examples:
              sharpimage clamp processed.png clamped.png
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, "Clamp",
                img =>
                {
                    SharpImage.Effects.UtilityOps.ClampImage(img);
                    return img;
                });
        });
        return cmd;
    }

    public static Command CreateCycleColormapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var shiftOpt = new Option<double>("--shift") { Description = "Color shift amount (0-1)", DefaultValueFactory = _ => 0.25 };
        var cmd = new Command("cycle", """
            Cycle (rotate) color values by the specified displacement amount.
            Creates a psychedelic color-shifting effect.

            Examples:
              sharpimage cycle photo.png cycled.png --shift 0.5
            """);
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(shiftOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double shift = parseResult.GetValue(shiftOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, $"Cycle Colormap (shift={shift:F2})",
                img =>
                {
                    SharpImage.Effects.UtilityOps.CycleColormap(img, shift);
                    return img;
                });
        });
        return cmd;
    }

    public static Command CreateStripCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("strip", """
            Remove all metadata (EXIF, ICC profile, XMP, IPTC) from an image.
            Produces a clean image with minimal file size.

            Examples:
              sharpimage strip photo.jpg clean.jpg
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            CliOutput.RunPipeline(input, output, "Strip Metadata",
                img =>
                {
                    img.StripMetadata();
                    return img;
                });
        });
        return cmd;
    }
}
