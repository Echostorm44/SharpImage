// SharpImage CLI — Enhancement commands.
// Histogram equalization, auto-level, auto-gamma, white balance, color LUT/grading, and more.

using SharpImage.Enhance;
using SharpImage.Formats;
using System.CommandLine;

namespace SharpImage.Cli;

public static class EnhanceCommands
{
    public static Command CreateEqualizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("equalize", """
            Histogram equalization — redistributes pixel intensities for full dynamic range.
            Produces maximum contrast by flattening the cumulative histogram.

            Examples:
              sharpimage equalize dark_photo.jpg equalized.png
              sharpimage equalize underexposed.png fixed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Histogram equalization",
                img => EnhanceOps.Equalize(img));
        });
        return cmd;
    }

    public static Command CreateNormalizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("normalize", """
            Linear stretch — maps the darkest pixel to black and the brightest to white.
            Uses a global min/max across all color channels for consistent results.

            Examples:
              sharpimage normalize faded.jpg normalized.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Normalize (linear stretch)",
                img => EnhanceOps.Normalize(img));
        });
        return cmd;
    }

    public static Command CreateAutoLevelCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("autolevel", """
            Per-channel auto level — stretches each channel independently to fill the full range.
            Better color correction than normalize for images with channel imbalance.

            Examples:
              sharpimage autolevel tinted.jpg corrected.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Auto level (per-channel stretch)",
                img => EnhanceOps.AutoLevel(img));
        });
        return cmd;
    }

    public static Command CreateAutoGammaCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("autogamma", """
            Automatic gamma correction — computes per-channel gamma from the log-mean.
            Adjusts midtones without clipping highlights or shadows.

            Examples:
              sharpimage autogamma dark_scene.jpg brightened.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Auto gamma correction",
                img => EnhanceOps.AutoGamma(img));
        });
        return cmd;
    }

    public static Command CreateSigmoidalContrastCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var contrastOpt = new Option<double>("-c", "--contrast") { Description = "Contrast strength (3-20, default 3)", DefaultValueFactory = _ => 3.0 };
        var midpointOpt = new Option<double>("-m", "--midpoint") { Description = "Midpoint (0.0-1.0, default 0.5)", DefaultValueFactory = _ => 0.5 };
        var unsharpenOpt = new Option<bool>("-u", "--unsharpen") { Description = "Unsharpen (decrease contrast) instead of sharpen" };
        var cmd = new Command("sigmoidal", """
            Sigmoidal (S-curve) contrast adjustment.
            Non-linear contrast that preserves highlights and shadows better than linear contrast.

            Examples:
              sharpimage sigmoidal photo.jpg -c 5 enhanced.png
              sharpimage sigmoidal photo.jpg -c 10 -m 0.3 --unsharpen softened.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(contrastOpt);
        cmd.Add(midpointOpt);
        cmd.Add(unsharpenOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double contrast = parseResult.GetValue(contrastOpt);
            double midpoint = parseResult.GetValue(midpointOpt);
            bool unsharpen = parseResult.GetValue(unsharpenOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Sigmoidal contrast (c={contrast:F1})",
                img => EnhanceOps.SigmoidalContrast(img, contrast, midpoint, !unsharpen));
        });
        return cmd;
    }

    public static Command CreateClaheCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var tilesXOpt = new Option<int>("--tiles-x") { Description = "Number of horizontal tiles (default 8)", DefaultValueFactory = _ => 8 };
        var tilesYOpt = new Option<int>("--tiles-y") { Description = "Number of vertical tiles (default 8)", DefaultValueFactory = _ => 8 };
        var clipOpt = new Option<double>("--clip") { Description = "Clip limit (default 2.0)", DefaultValueFactory = _ => 2.0 };
        var cmd = new Command("clahe", """
            Contrast Limited Adaptive Histogram Equalization (CLAHE).
            Enhances local contrast while limiting noise amplification.
            Works by dividing the image into tiles and equalizing each independently.

            Examples:
              sharpimage clahe photo.jpg enhanced.png
              sharpimage clahe xray.png --tiles-x 16 --tiles-y 16 --clip 3.0 enhanced.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(tilesXOpt);
        cmd.Add(tilesYOpt);
        cmd.Add(clipOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int tx = parseResult.GetValue(tilesXOpt);
            int ty = parseResult.GetValue(tilesYOpt);
            double clip = parseResult.GetValue(clipOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"CLAHE ({tx}×{ty} tiles, clip={clip:F1})",
                img => EnhanceOps.CLAHE(img, tx, ty, clip));
        });
        return cmd;
    }

    public static Command CreateModulateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var brightnessOpt = new Option<double>("-b", "--brightness") { Description = "Brightness % (100=unchanged, 150=50% brighter)", DefaultValueFactory = _ => 100.0 };
        var saturationOpt = new Option<double>("-s", "--saturation") { Description = "Saturation % (100=unchanged, 0=grayscale)", DefaultValueFactory = _ => 100.0 };
        var hueOpt = new Option<double>("-h", "--hue") { Description = "Hue rotation % (100=unchanged, 200=+180°)", DefaultValueFactory = _ => 100.0 };
        var cmd = new Command("modulate", """
            Modulate brightness, saturation, and hue as percentages.
            Compatible with ImageMagick's -modulate syntax.
            100 = no change, <100 = decrease, >100 = increase.

            Examples:
              sharpimage modulate photo.jpg -b 120 -s 130 brighter_vivid.png
              sharpimage modulate photo.jpg -h 200 hue_rotated.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(brightnessOpt);
        cmd.Add(saturationOpt);
        cmd.Add(hueOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double b = parseResult.GetValue(brightnessOpt);
            double s = parseResult.GetValue(saturationOpt);
            double h = parseResult.GetValue(hueOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Modulate (B={b:F0}%, S={s:F0}%, H={h:F0}%)",
                img => EnhanceOps.Modulate(img, b, s, h));
        });
        return cmd;
    }

    public static Command CreateWhiteBalanceCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("whitebalance", """
            Automatic white balance using the Gray World assumption.
            Corrects color casts by scaling channels to match the overall mean luminance.

            Examples:
              sharpimage whitebalance indoor.jpg corrected.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "White balance (Gray World)",
                img => EnhanceOps.WhiteBalance(img));
        });
        return cmd;
    }

    public static Command CreateColorizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var redOpt = new Option<int>("-r", "--red") { Description = "Red tint (0-255)", DefaultValueFactory = _ => 255 };
        var greenOpt = new Option<int>("-g", "--green") { Description = "Green tint (0-255)", DefaultValueFactory = _ => 200 };
        var blueOpt = new Option<int>("-b", "--blue") { Description = "Blue tint (0-255)", DefaultValueFactory = _ => 100 };
        var amountOpt = new Option<double>("-a", "--amount") { Description = "Tint strength (0.0-1.0)", DefaultValueFactory = _ => 0.3 };
        var cmd = new Command("colorize", """
            Apply a color tint to an image while preserving luminance.
            Blends the tint color with the original based on the amount parameter.

            Examples:
              sharpimage colorize photo.jpg -r 255 -g 200 -b 100 -a 0.5 warm.png
              sharpimage colorize photo.jpg -r 100 -g 150 -b 255 -a 0.3 cool.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(redOpt);
        cmd.Add(greenOpt);
        cmd.Add(blueOpt);
        cmd.Add(amountOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            byte r = (byte)parseResult.GetValue(redOpt);
            byte g = (byte)parseResult.GetValue(greenOpt);
            byte b = (byte)parseResult.GetValue(blueOpt);
            double amount = parseResult.GetValue(amountOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Colorize (R={r} G={g} B={b}, {amount:P0})",
                img => EnhanceOps.Colorize(img, r, g, b, amount));
        });
        return cmd;
    }

    public static Command CreateSolarizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var thresholdOpt = new Option<double>("-t", "--threshold") { Description = "Threshold (0.0-1.0, default 0.5). Pixels above this are inverted.", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("solarize", """
            Solarize effect — inverts pixel values above a threshold.
            Creates a partial negative effect reminiscent of the Sabattier darkroom process.

            Examples:
              sharpimage solarize photo.jpg solarized.png
              sharpimage solarize photo.jpg -t 0.3 heavy_solarize.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(thresholdOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double threshold = parseResult.GetValue(thresholdOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Solarize (threshold={threshold:F2})",
                img => EnhanceOps.Solarize(img, threshold));
        });
        return cmd;
    }

    public static Command CreateSepiaCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var thresholdOpt = new Option<double>("-t", "--threshold") { Description = "Sepia intensity (0.0-1.0, default 0.8). Higher = stronger sepia.", DefaultValueFactory = _ => 0.8 };
        var cmd = new Command("sepia", """
            Apply sepia tone for a vintage warm brown look.
            Uses standard sepia matrix coefficients with adjustable intensity.

            Examples:
              sharpimage sepia photo.jpg vintage.png
              sharpimage sepia photo.jpg -t 0.5 subtle_sepia.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(thresholdOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double threshold = parseResult.GetValue(thresholdOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Sepia tone (intensity={threshold:F2})",
                img => EnhanceOps.SepiaTone(img, threshold));
        });
        return cmd;
    }

    public static Command CreateLocalContrastCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius")
        {
            Description = "Blur radius for local mean (default 10)",
            DefaultValueFactory = _ => 10
        };
        var strengthOpt = new Option<double>("-s", "--strength")
        {
            Description = "Contrast boost strength 0-100 (default 50)",
            DefaultValueFactory = _ => 50.0
        };

        var cmd = new Command("localcontrast", """
            Local contrast enhancement using unsharp luminance masking.
            Boosts micro-contrast without affecting global tonal range.
            
            Examples:
              sharpimage localcontrast photo.jpg enhanced.png
              sharpimage localcontrast photo.jpg -r 15 -s 75 enhanced.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(strengthOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            double strength = parseResult.GetValue(strengthOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Local contrast (r={radius}, strength={strength:F0}%)",
                img => EnhanceOps.LocalContrast(img, radius, strength));
        });
        return cmd;
    }

    public static Command CreateClutCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var clutArg = new Argument<string>("clut") { Description = "CLUT image file path (1D gradient)" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };

        var cmd = new Command("clut", """
            Apply a 1D color lookup table (CLUT) image.
            The CLUT image is sampled diagonally to remap colors.
            
            Examples:
              sharpimage clut photo.jpg warm_lut.png result.png
              sharpimage clut photo.jpg grayscale_ramp.png result.png
            """);
        cmd.Add(inputArg);
        cmd.Add(clutArg);
        cmd.Add(outputArg);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string clutPath = parseResult.GetValue(clutArg)!;
            string output = parseResult.GetValue(outputArg)!;

            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(clutPath)) return;

            var clutImage = FormatRegistry.Read(clutPath);
            CliOutput.RunPipeline(input, output,
                "Apply CLUT",
                img => EnhanceOps.ApplyClut(img, clutImage));
        });
        return cmd;
    }

    public static Command CreateHaldClutCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var haldArg = new Argument<string>("hald") { Description = "Hald CLUT image file path (3D LUT)" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };

        var cmd = new Command("haldclut", """
            Apply a Hald CLUT (3D color lookup table) for color grading.
            The Hald image encodes a full 3D RGB color transform.
            
            Examples:
              sharpimage haldclut photo.jpg film_emulation.png result.png
              sharpimage haldclut photo.jpg vintage_hald.png result.png
            """);
        cmd.Add(inputArg);
        cmd.Add(haldArg);
        cmd.Add(outputArg);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string haldPath = parseResult.GetValue(haldArg)!;
            string output = parseResult.GetValue(outputArg)!;

            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(haldPath)) return;

            var haldImage = FormatRegistry.Read(haldPath);
            CliOutput.RunPipeline(input, output,
                "Apply Hald CLUT",
                img => EnhanceOps.ApplyHaldClut(img, haldImage));
        });
        return cmd;
    }

    public static Command CreateLevelizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var blackOpt = new Option<double>("-b", "--black")
        {
            Description = "Black point (0-65535, default 0)",
            DefaultValueFactory = _ => 0.0
        };
        var whiteOpt = new Option<double>("-w", "--white")
        {
            Description = "White point (0-65535, default 65535)",
            DefaultValueFactory = _ => 65535.0
        };
        var gammaOpt = new Option<double>("-g", "--gamma")
        {
            Description = "Gamma correction (default 1.0)",
            DefaultValueFactory = _ => 1.0
        };

        var cmd = new Command("levelize", """
            Inverse level: maps [0, max] to [black, white] with gamma.
            Compresses the tonal range into the specified output range.
            
            Examples:
              sharpimage levelize photo.jpg -b 10000 -w 55000 result.png
              sharpimage levelize photo.jpg -b 5000 -w 60000 -g 1.2 result.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(blackOpt);
        cmd.Add(whiteOpt);
        cmd.Add(gammaOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double black = parseResult.GetValue(blackOpt);
            double white = parseResult.GetValue(whiteOpt);
            double gamma = parseResult.GetValue(gammaOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Levelize (b={black:F0}, w={white:F0}, γ={gamma:F2})",
                img => EnhanceOps.Levelize(img, black, white, gamma));
        });
        return cmd;
    }

    public static Command CreateContrastStretchCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var blackOpt = new Option<double>("-b", "--black-percent")
        {
            Description = "Black clip percentage (default 0.15%)",
            DefaultValueFactory = _ => 0.15
        };
        var whiteOpt = new Option<double>("-w", "--white-percent")
        {
            Description = "White clip percentage (default 0.05%)",
            DefaultValueFactory = _ => 0.05
        };

        var cmd = new Command("contraststretch", """
            Histogram-based contrast stretch.
            Clips the specified percentage from each end and remaps.
            
            Examples:
              sharpimage contraststretch photo.jpg enhanced.png
              sharpimage contraststretch photo.jpg -b 1 -w 1 enhanced.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(blackOpt);
        cmd.Add(whiteOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double black = parseResult.GetValue(blackOpt);
            double white = parseResult.GetValue(whiteOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Contrast stretch (b={black:F2}%, w={white:F2}%)",
                img => EnhanceOps.ContrastStretch(img, black, white));
        });
        return cmd;
    }

    public static Command CreateLinearStretchCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var blackOpt = new Option<double>("-b", "--black-percent")
        {
            Description = "Black clip percentage (default 2%)",
            DefaultValueFactory = _ => 2.0
        };
        var whiteOpt = new Option<double>("-w", "--white-percent")
        {
            Description = "White clip percentage (default 1%)",
            DefaultValueFactory = _ => 1.0
        };

        var cmd = new Command("linearstretch", """
            Linear histogram stretch based on intensity.
            Finds black/white levels at percentile thresholds and levels linearly.
            
            Examples:
              sharpimage linearstretch photo.jpg enhanced.png
              sharpimage linearstretch photo.jpg -b 3 -w 2 enhanced.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(blackOpt);
        cmd.Add(whiteOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double black = parseResult.GetValue(blackOpt);
            double white = parseResult.GetValue(whiteOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Linear stretch (b={black:F1}%, w={white:F1}%)",
                img => EnhanceOps.LinearStretch(img, black, white));
        });
        return cmd;
    }

    // ─── Bundle C: Curves, Dodge/Burn, Exposure, Vibrance, Dehaze ──

    public static Command CreateCurvesCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var pointsOpt = new Option<string>("--points") { Description = "Control points as 'in:out,in:out,...' e.g. '0:0,0.25:0.4,0.75:0.9,1:1'", Required = true };
        var cmd = new Command("curves", "Apply a custom tone curve to an image.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(pointsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string pointsStr = parseResult.GetValue(pointsOpt)!;

            var controlPoints = pointsStr.Split(',')
                .Select(p => p.Split(':'))
                .Select(p => (double.Parse(p[0].Trim()), double.Parse(p[1].Trim())))
                .ToArray();

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Curves",
                img => SharpImage.Effects.ColorAdjust.Curves(img, controlPoints));
        });
        return cmd;
    }

    public static Command CreateDodgeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var exposureOpt = new Option<double>("--exposure") { Description = "Dodge strength (0-1)", DefaultValueFactory = _ => 0.5 };
        var rangeOpt = new Option<double>("--range") { Description = "Tonal target (0=shadows, 0.5=mids, 1=highlights)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("dodge", "Lighten (dodge) selected tonal range.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(exposureOpt); cmd.Add(rangeOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double exp = parseResult.GetValue(exposureOpt);
            double range = parseResult.GetValue(rangeOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Dodge",
                img => SharpImage.Effects.ColorAdjust.Dodge(img, exp, range));
        });
        return cmd;
    }

    public static Command CreateBurnCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var exposureOpt = new Option<double>("--exposure") { Description = "Burn strength (0-1)", DefaultValueFactory = _ => 0.5 };
        var rangeOpt = new Option<double>("--range") { Description = "Tonal target (0=shadows, 0.5=mids, 1=highlights)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("burn", "Darken (burn) selected tonal range.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(exposureOpt); cmd.Add(rangeOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double exp = parseResult.GetValue(exposureOpt);
            double range = parseResult.GetValue(rangeOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Burn",
                img => SharpImage.Effects.ColorAdjust.Burn(img, exp, range));
        });
        return cmd;
    }

    public static Command CreateExposureCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var evOpt = new Option<double>("--ev") { Description = "Exposure value in EV stops (+/- range)", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("exposure", "Adjust exposure in EV stops.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(evOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double ev = parseResult.GetValue(evOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Exposure ({ev:+0.0;-0.0} EV)",
                img => SharpImage.Effects.ColorAdjust.Exposure(img, ev));
        });
        return cmd;
    }

    public static Command CreateVibranceCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var amountOpt = new Option<double>("--amount") { Description = "Vibrance amount (-1 to +1)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("vibrance", "Adjust vibrance (smart saturation).");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(amountOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double amount = parseResult.GetValue(amountOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Vibrance",
                img => SharpImage.Effects.ColorAdjust.Vibrance(img, amount));
        });
        return cmd;
    }

    public static Command CreateDehazeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var strengthOpt = new Option<double>("--strength") { Description = "Dehaze strength (0-1)", DefaultValueFactory = _ => 0.5 };
        var patchOpt = new Option<int>("--patch-radius") { Description = "Dark channel patch radius", DefaultValueFactory = _ => 7 };
        var cmd = new Command("dehaze", "Remove haze using dark channel prior.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(strengthOpt); cmd.Add(patchOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double strength = parseResult.GetValue(strengthOpt);
            int patch = parseResult.GetValue(patchOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Dehaze",
                img => SharpImage.Effects.ColorAdjust.Dehaze(img, strength, patch));
        });
        return cmd;
    }

    // ─── Bundle I: Color Grading & Creative ─────────────────────────

    public static Command CreateColorTransferCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var refArg = new Argument<string>("reference") { Description = "Reference image (color source)" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var strengthOpt = new Option<double>("--strength") { Description = "Transfer strength (0-1)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("colortransfer", "Transfer colors from a reference image (Reinhard method).");
        cmd.Add(inputArg); cmd.Add(refArg); cmd.Add(outputArg); cmd.Add(strengthOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string refPath = parseResult.GetValue(refArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double str = parseResult.GetValue(strengthOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(refPath)) return;

            var src = FormatRegistry.Read(input);
            var refImg = FormatRegistry.Read(refPath);
            var result = SharpImage.Effects.ColorGradingOps.ColorTransfer(src, refImg, str);
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); refImg.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateSplitToningCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var shadowOpt = new Option<string>("--shadow") { Description = "Shadow color R,G,B", Required = true };
        var highlightOpt = new Option<string>("--highlight") { Description = "Highlight color R,G,B", Required = true };
        var balanceOpt = new Option<double>("--balance") { Description = "Balance (0-1)", DefaultValueFactory = _ => 0.5 };
        var strengthOpt = new Option<double>("--strength") { Description = "Strength (0-1)", DefaultValueFactory = _ => 0.3 };
        var cmd = new Command("splittone", "Apply split toning with separate shadow/highlight colors.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(shadowOpt); cmd.Add(highlightOpt);
        cmd.Add(balanceOpt); cmd.Add(strengthOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var (sr, sg, sb) = EffectsCommands.ParseColor(parseResult.GetValue(shadowOpt)!);
            var (hr, hg, hb) = EffectsCommands.ParseColor(parseResult.GetValue(highlightOpt)!);
            double balance = parseResult.GetValue(balanceOpt);
            double str = parseResult.GetValue(strengthOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Split toning",
                img => SharpImage.Effects.ColorGradingOps.SplitToning(img, sr, sg, sb, hr, hg, hb, balance, str));
        });
        return cmd;
    }

    public static Command CreateGradientMapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var stopsOpt = new Option<string>("--stops") { Description = "Gradient stops: 'pos:R,G,B;pos:R,G,B;...' e.g. '0:0,0,65535;1:65535,0,0'", Required = true };
        var cmd = new Command("gradmap", "Map luminance to a color gradient.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(stopsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string stopsStr = parseResult.GetValue(stopsOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            var stopParts = stopsStr.Split(';');
            var stops = new (double, ushort, ushort, ushort)[stopParts.Length];
            for (int i = 0; i < stopParts.Length; i++)
            {
                var parts = stopParts[i].Split(':');
                double pos = double.Parse(parts[0]);
                var rgb = parts[1].Split(',');
                stops[i] = (pos, ushort.Parse(rgb[0]), ushort.Parse(rgb[1]), ushort.Parse(rgb[2]));
            }

            CliOutput.RunPipeline(input, output, "Gradient map",
                img => SharpImage.Effects.ColorGradingOps.GradientMap(img, stops));
        });
        return cmd;
    }

    public static Command CreateChannelMixerCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var matrixOpt = new Option<string>("--matrix") { Description = "3x3 matrix: 'rr,rg,rb,gr,gg,gb,br,bg,bb'", Required = true };
        var cmd = new Command("channelmix", "Mix RGB channels using a 3x3 matrix.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(matrixOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var vals = parseResult.GetValue(matrixOpt)!.Split(',').Select(double.Parse).ToArray();
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Channel mix",
                img => SharpImage.Effects.ColorGradingOps.ChannelMixer(img,
                    vals[0], vals[1], vals[2], vals[3], vals[4], vals[5], vals[6], vals[7], vals[8]));
        });
        return cmd;
    }

    public static Command CreatePhotoFilterCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var colorOpt = new Option<string>("--color") { Description = "Filter color R,G,B", Required = true };
        var densityOpt = new Option<double>("--density") { Description = "Filter density (0-1)", DefaultValueFactory = _ => 0.25 };
        var cmd = new Command("photofilter", "Apply a warming/cooling photo filter.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(colorOpt); cmd.Add(densityOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var (r, g, b) = EffectsCommands.ParseColor(parseResult.GetValue(colorOpt)!);
            double density = parseResult.GetValue(densityOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Photo filter",
                img => SharpImage.Effects.ColorGradingOps.PhotoFilter(img, r, g, b, density));
        });
        return cmd;
    }

    public static Command CreateDuotoneCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var darkOpt = new Option<string>("--dark") { Description = "Shadow color R,G,B", Required = true };
        var lightOpt = new Option<string>("--light") { Description = "Highlight color R,G,B", Required = true };
        var cmd = new Command("duotone", "Apply duotone effect mapping shadows/highlights to two colors.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(darkOpt); cmd.Add(lightOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var (dr, dg, db) = EffectsCommands.ParseColor(parseResult.GetValue(darkOpt)!);
            var (lr, lg, lb) = EffectsCommands.ParseColor(parseResult.GetValue(lightOpt)!);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Duotone",
                img => SharpImage.Effects.ColorGradingOps.Duotone(img, dr, dg, db, lr, lg, lb));
        });
        return cmd;
    }

    // ─── Bundle N: Accessibility & Print ────────────────────────

    public static Command CreateColorBlindSimCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var typeOpt = new Option<string>("--type") { Description = "Type: protanopia, deuteranopia, tritanopia", DefaultValueFactory = _ => "protanopia" };
        var cmd = new Command("colorblindsim", "Simulate color vision deficiency.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(typeOpt);
        cmd.SetAction((parseResult) =>
        {
            var typeName = parseResult.GetValue(typeOpt)!;
            var cbType = typeName.ToLowerInvariant() switch
            {
                "deuteranopia" or "deutan" => SharpImage.Effects.ColorBlindnessType.Deuteranopia,
                "tritanopia" or "tritan" => SharpImage.Effects.ColorBlindnessType.Tritanopia,
                _ => SharpImage.Effects.ColorBlindnessType.Protanopia
            };
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Color Blind Sim", img =>
                SharpImage.Effects.AccessibilityOps.SimulateColorBlindness(img, cbType));
        });
        return cmd;
    }

    public static Command CreateDaltonizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var typeOpt = new Option<string>("--type") { Description = "Type: protanopia, deuteranopia, tritanopia", DefaultValueFactory = _ => "protanopia" };
        var strengthOpt = new Option<double>("--strength") { Description = "Compensation strength (0-1)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("daltonize", "Compensate image for color vision deficiency.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(typeOpt); cmd.Add(strengthOpt);
        cmd.SetAction((parseResult) =>
        {
            var typeName = parseResult.GetValue(typeOpt)!;
            var cbType = typeName.ToLowerInvariant() switch
            {
                "deuteranopia" or "deutan" => SharpImage.Effects.ColorBlindnessType.Deuteranopia,
                "tritanopia" or "tritan" => SharpImage.Effects.ColorBlindnessType.Tritanopia,
                _ => SharpImage.Effects.ColorBlindnessType.Protanopia
            };
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Daltonize", img =>
                SharpImage.Effects.AccessibilityOps.Daltonize(img, cbType, parseResult.GetValue(strengthOpt)));
        });
        return cmd;
    }

    public static Command CreateSoftProofCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var inkOpt = new Option<double>("--ink") { Description = "Max ink coverage (0-4)", DefaultValueFactory = _ => 3.0 };
        var blackOpt = new Option<double>("--black") { Description = "Black generation (0-1)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("softproof", "Simulate CMYK print output from RGB.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(inkOpt); cmd.Add(blackOpt);
        cmd.SetAction((parseResult) =>
        {
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Soft Proof", img =>
                SharpImage.Effects.AccessibilityOps.SoftProof(img, parseResult.GetValue(inkOpt), parseResult.GetValue(blackOpt)));
        });
        return cmd;
    }

    public static Command CreateLevelColorsCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var blackOpt = new Option<string>("--black") { Description = "Black point color as 'R,G,B' (0-1 each)", DefaultValueFactory = _ => "0,0,0" };
        var whiteOpt = new Option<string>("--white") { Description = "White point color as 'R,G,B' (0-1 each)", DefaultValueFactory = _ => "1,1,1" };

        var cmd = new Command("levelcolors", """
            Map the black point to a specific color and white point to another,
            with linear interpolation for intermediate values. Creates tinted effects.

            Examples:
              sharpimage levelcolors photo.png tinted.png --black "0.1,0,0.2" --white "1,0.9,0.8"
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(blackOpt); cmd.Add(whiteOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            var b = parseResult.GetValue(blackOpt)!.Split(',').Select(double.Parse).ToArray();
            var w = parseResult.GetValue(whiteOpt)!.Split(',').Select(double.Parse).ToArray();
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                "Level Colors",
                img => SharpImage.Enhance.EnhanceOps.LevelColors(img, b[0], b[1], b[2], w[0], w[1], w[2]));
        });
        return cmd;
    }
}
