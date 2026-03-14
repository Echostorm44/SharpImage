// SharpImage CLI — Blur and noise commands.
// Motion blur, radial blur, selective blur, noise, despeckle, wavelet denoise,
// median filter, bilateral blur, kuwahara filter, adaptive blur/sharpen.

using SharpImage.Effects;
using System.CommandLine;

namespace SharpImage.Cli;

public static class BlurNoiseCommands
{
    public static Command CreateMotionBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Blur length in pixels (default 10)", DefaultValueFactory = _ => 10 };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Gaussian sigma (default 3.0)", DefaultValueFactory = _ => 3.0 };
        var angleOpt = new Option<double>("-a", "--angle") { Description = "Direction angle in degrees (default 0 = horizontal)", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("motionblur", """
            Directional motion blur along a specified angle.
            Simulates camera or subject motion in a specific direction.

            Examples:
              sharpimage motionblur photo.jpg blurred.png
              sharpimage motionblur photo.jpg -r 20 -s 5 -a 45 diagonal_blur.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(sigmaOpt);
        cmd.Add(angleOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            double sigma = parseResult.GetValue(sigmaOpt);
            double angle = parseResult.GetValue(angleOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Motion blur (r={radius}, σ={sigma:F1}, {angle:F0}°)",
                img => BlurNoiseOps.MotionBlur(img, radius, sigma, angle));
        });
        return cmd;
    }

    public static Command CreateRadialBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var angleOpt = new Option<double>("-a", "--angle") { Description = "Blur angle in degrees (default 10)", DefaultValueFactory = _ => 10.0 };
        var cmd = new Command("radialblur", """
            Rotational (radial/spin) blur around the image center.
            Creates a spinning or rotating effect. Higher angle = more blur.

            Examples:
              sharpimage radialblur photo.jpg spun.png
              sharpimage radialblur photo.jpg -a 30 heavy_spin.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(angleOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double angle = parseResult.GetValue(angleOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Radial blur ({angle:F1}°)",
                img => BlurNoiseOps.RadialBlur(img, angle));
        });
        return cmd;
    }

    public static Command CreateSelectiveBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Blur radius in pixels (default 2)", DefaultValueFactory = _ => 2 };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Gaussian sigma (default 1.0)", DefaultValueFactory = _ => 1.0 };
        var thresholdOpt = new Option<double>("-t", "--threshold") { Description = "Contrast threshold 0.0-1.0 (default 0.1). Only neighbors within this contrast are blurred.", DefaultValueFactory = _ => 0.1 };
        var cmd = new Command("selectiveblur", """
            Selective blur — smooths only where local contrast is below a threshold.
            Preserves hard edges while reducing noise in smooth areas.

            Examples:
              sharpimage selectiveblur noisy.jpg cleaned.png
              sharpimage selectiveblur photo.jpg -r 3 -s 2 -t 0.2 denoised.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(sigmaOpt);
        cmd.Add(thresholdOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            double sigma = parseResult.GetValue(sigmaOpt);
            double threshold = parseResult.GetValue(thresholdOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Selective blur (r={radius}, t={threshold:F2})",
                img => BlurNoiseOps.SelectiveBlur(img, radius, sigma, threshold));
        });
        return cmd;
    }

    public static Command CreateAddNoiseCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var typeOpt = new Option<string>("-t", "--type") { Description = "Noise type: uniform, gaussian, impulse, laplacian, multiplicative, poisson, random (default gaussian)", DefaultValueFactory = _ => "gaussian" };
        var attenuateOpt = new Option<double>("-a", "--attenuate") { Description = "Noise strength (default 1.0)", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("noise", """
            Add noise of a specified type to the image.
            Supports 7 noise types matching ImageMagick's noise generators.

            Noise types:
              uniform         - Uniform random distribution
              gaussian        - Gaussian (normal) distribution (Box-Muller)
              impulse         - Salt-and-pepper noise
              laplacian       - Laplacian distribution
              multiplicative  - Multiplicative Gaussian
              poisson         - Poisson distribution
              random          - Full-range random values

            Examples:
              sharpimage noise photo.jpg noisy.png
              sharpimage noise photo.jpg -t impulse -a 0.5 saltpepper.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(typeOpt);
        cmd.Add(attenuateOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string typeName = parseResult.GetValue(typeOpt)!;
            double attenuate = parseResult.GetValue(attenuateOpt);

            NoiseType noiseType = typeName.ToLowerInvariant() switch
            {
                "uniform" => NoiseType.Uniform,
                "gaussian" => NoiseType.Gaussian,
                "impulse" => NoiseType.Impulse,
                "laplacian" => NoiseType.Laplacian,
                "multiplicative" => NoiseType.MultiplicativeGaussian,
                "poisson" => NoiseType.Poisson,
                "random" => NoiseType.Random,
                _ => NoiseType.Gaussian
            };

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Add noise ({noiseType})",
                img => BlurNoiseOps.AddNoise(img, noiseType, attenuate));
        });
        return cmd;
    }

    public static Command CreateDespeckleCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("despeckle", """
            Remove speckle noise using Crimmins complementary hulling.
            Effective for impulsive (salt-and-pepper) noise while preserving edges.

            Examples:
              sharpimage despeckle noisy_scan.jpg cleaned.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Despeckle (Crimmins hulling)",
                img => BlurNoiseOps.Despeckle(img));
        });
        return cmd;
    }

    public static Command CreateWaveletDenoiseCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var thresholdOpt = new Option<double>("-t", "--threshold") { Description = "Noise threshold 0.0-1.0 (default 0.3)", DefaultValueFactory = _ => 0.3 };
        var softnessOpt = new Option<double>("-s", "--softness") { Description = "Attenuation softness 0.0-1.0 (default 0.0). 0=hard, 1=gradual.", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("waveletdenoise", """
            Multi-scale wavelet-based noise reduction.
            Decomposes the image into frequency bands and soft-thresholds noise.
            Better at preserving detail than simple blur-based denoising.

            Examples:
              sharpimage waveletdenoise noisy.jpg cleaned.png
              sharpimage waveletdenoise photo.jpg -t 0.5 -s 0.3 smooth.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(thresholdOpt);
        cmd.Add(softnessOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double threshold = parseResult.GetValue(thresholdOpt);
            double softness = parseResult.GetValue(softnessOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Wavelet denoise (t={threshold:F2})",
                img => BlurNoiseOps.WaveletDenoise(img, threshold, softness));
        });
        return cmd;
    }

    public static Command CreateSpreadCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<double>("-r", "--radius") { Description = "Maximum displacement radius in pixels (default 5.0)", DefaultValueFactory = _ => 5.0 };
        var cmd = new Command("spread", """
            Scatter each pixel randomly within a given radius.
            Produces a grainy dissolve/static effect.

            Examples:
              sharpimage spread photo.jpg scattered.png
              sharpimage spread photo.jpg -r 10 heavy_scatter.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Spread (r={radius:F1})",
                img => BlurNoiseOps.Spread(img, radius));
        });
        return cmd;
    }

    public static Command CreateMedianCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius")
        {
            Description = "Filter radius in pixels (default 1)",
            DefaultValueFactory = _ => 1
        };

        var cmd = new Command("median", """
            Median filter for impulse (salt-and-pepper) noise removal.
            Replaces each pixel with the median of its NxN neighborhood.
            
            Examples:
              sharpimage median noisy.jpg cleaned.png
              sharpimage median noisy.jpg -r 2 cleaned.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Median filter (r={radius})",
                img => BlurNoiseOps.MedianFilter(img, radius));
        });
        return cmd;
    }

    public static Command CreateBilateralCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius")
        {
            Description = "Filter radius in pixels (default 3)",
            DefaultValueFactory = _ => 3
        };
        var spatialOpt = new Option<double>("-s", "--spatial-sigma")
        {
            Description = "Spatial domain sigma (default 1.5)",
            DefaultValueFactory = _ => 1.5
        };
        var rangeOpt = new Option<double>("--range-sigma")
        {
            Description = "Range/intensity domain sigma (default 50.0)",
            DefaultValueFactory = _ => 50.0
        };

        var cmd = new Command("bilateral", """
            Edge-preserving bilateral blur.
            Smooths noise while keeping sharp edges intact by weighting
            neighbors by both spatial distance and intensity similarity.
            
            Examples:
              sharpimage bilateral photo.jpg smoothed.png
              sharpimage bilateral photo.jpg -r 5 -s 2.0 --range-sigma 30 smoothed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(spatialOpt);
        cmd.Add(rangeOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            double spatial = parseResult.GetValue(spatialOpt);
            double range = parseResult.GetValue(rangeOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Bilateral blur (r={radius}, σs={spatial:F1}, σr={range:F1})",
                img => BlurNoiseOps.BilateralBlur(img, radius, spatial, range));
        });
        return cmd;
    }

    public static Command CreateKuwaharaCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius")
        {
            Description = "Filter radius in pixels (default 2)",
            DefaultValueFactory = _ => 2
        };

        var cmd = new Command("kuwahara", """
            Kuwahara filter for painterly noise reduction.
            Selects the most uniform quadrant around each pixel,
            producing a distinctive oil-painting effect.
            
            Examples:
              sharpimage kuwahara photo.jpg painting.png
              sharpimage kuwahara photo.jpg -r 4 painting.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Kuwahara filter (r={radius})",
                img => BlurNoiseOps.KuwaharaFilter(img, radius));
        });
        return cmd;
    }

    public static Command CreateAdaptiveBlurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma")
        {
            Description = "Max blur sigma (default 2.0)",
            DefaultValueFactory = _ => 2.0
        };

        var cmd = new Command("adaptiveblur", """
            Adaptive blur that varies strength by local edge content.
            Flat regions are blurred more; edges are preserved.
            
            Examples:
              sharpimage adaptiveblur photo.jpg smoothed.png
              sharpimage adaptiveblur photo.jpg -s 3.0 smoothed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Adaptive blur (σ={sigma:F1})",
                img => ConvolutionFilters.AdaptiveBlur(img, sigma));
        });
        return cmd;
    }

    public static Command CreateAdaptiveSharpenCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma")
        {
            Description = "Unsharp mask sigma (default 1.0)",
            DefaultValueFactory = _ => 1.0
        };
        var amountOpt = new Option<double>("-a", "--amount")
        {
            Description = "Max sharpen amount (default 2.0)",
            DefaultValueFactory = _ => 2.0
        };

        var cmd = new Command("adaptivesharpen", """
            Adaptive sharpen that increases strength near edges.
            Enhances detail at edges without amplifying noise in flat areas.
            
            Examples:
              sharpimage adaptivesharpen photo.jpg sharp.png
              sharpimage adaptivesharpen photo.jpg -s 1.5 -a 3.0 sharp.png
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

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Adaptive sharpen (σ={sigma:F1}, amount={amount:F1})",
                img => ConvolutionFilters.AdaptiveSharpen(img, sigma, amount));
        });
        return cmd;
    }

    public static Command CreateModeFilterCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius")
        {
            Description = "Filter radius in pixels (default 1 = 3×3)",
            DefaultValueFactory = _ => 1
        };

        var cmd = new Command("modefilter", """
            Mode filter — replaces each pixel with the most frequent value
            in its neighborhood. Good for removing impulse noise while
            preserving uniform regions better than median filter.

            Examples:
              sharpimage modefilter photo.png cleaned.png
              sharpimage modefilter photo.png -r 2 cleaned.png
            """);
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"Mode filter (r={radius})",
                img => BlurNoiseOps.ModeFilter(img, radius));
        });
        return cmd;
    }
}
