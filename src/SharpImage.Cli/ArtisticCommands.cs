// SharpImage CLI — Artistic effect commands.
// Oil paint, charcoal, sketch, vignette, wave, swirl, implode.

using SharpImage.Effects;
using System.CommandLine;

namespace SharpImage.Cli;

public static class ArtisticCommands
{
    public static Command CreateOilPaintCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Neighborhood radius in pixels (default 3)", DefaultValueFactory = _ => 3 };
        var levelsOpt = new Option<int>("-l", "--levels") { Description = "Number of intensity levels for quantization (default 256)", DefaultValueFactory = _ => 256 };
        var cmd = new Command("oilpaint", """
            Simulates an oil painting effect.
            Replaces each pixel with the most frequent color in its neighborhood,
            creating flat color regions with visible brush-stroke boundaries.

            Examples:
              sharpimage oilpaint photo.jpg painted.png
              sharpimage oilpaint photo.jpg -r 5 -l 20 heavy_paint.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int radius = parseResult.GetValue(radiusOpt);
            int levels = parseResult.GetValue(levelsOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Oil paint (r={radius}, levels={levels})",
                img => ArtisticOps.OilPaint(img, radius, levels));
        });
        return cmd;
    }

    public static Command CreateCharcoalCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var radiusOpt = new Option<double>("-r", "--radius") { Description = "Blur radius (default 1.0)", DefaultValueFactory = _ => 1.0 };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Gaussian sigma (default 0.5)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("charcoal", """
            Creates a charcoal sketch effect.
            Applies edge detection, blur, normalization, negation, and grayscale
            to produce a hand-drawn charcoal appearance.

            Examples:
              sharpimage charcoal photo.jpg sketch.png
              sharpimage charcoal photo.jpg -r 2 -s 1.0 thick_lines.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(radiusOpt);
        cmd.Add(sigmaOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double radius = parseResult.GetValue(radiusOpt);
            double sigma = parseResult.GetValue(sigmaOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Charcoal (r={radius:F1}, σ={sigma:F1})",
                img => ArtisticOps.Charcoal(img, radius, sigma));
        });
        return cmd;
    }

    public static Command CreateSketchCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Stroke width sigma (default 0.5)", DefaultValueFactory = _ => 0.5 };
        var angleOpt = new Option<double>("-a", "--angle") { Description = "Stroke direction in degrees (default 45)", DefaultValueFactory = _ => 45.0 };
        var cmd = new Command("sketch", """
            Creates a pencil sketch effect.
            Uses directional motion blur of edge detection to simulate
            pencil strokes at a specified angle.

            Examples:
              sharpimage sketch photo.jpg pencil.png
              sharpimage sketch photo.jpg -s 1.0 -a 135 cross_hatch.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(angleOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            double angle = parseResult.GetValue(angleOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Sketch (σ={sigma:F1}, {angle:F0}°)",
                img => ArtisticOps.Sketch(img, sigma, angle));
        });
        return cmd;
    }

    public static Command CreateVignetteCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("-s", "--sigma") { Description = "Edge softness sigma (default 10.0)", DefaultValueFactory = _ => 10.0 };
        var xOpt = new Option<int?>("-x", "--xoffset") { Description = "Horizontal inset (default 10% of width, matching IM)" };
        var yOpt = new Option<int?>("-y", "--yoffset") { Description = "Vertical inset (default 10% of height, matching IM)" };
        var cmd = new Command("vignette", """
            Applies a vignette darkening effect to image edges.
            Smoothly darkens pixels toward the edges with an elliptical falloff.

            Examples:
              sharpimage vignette photo.jpg vignetted.png
              sharpimage vignette photo.jpg -s 5 -x 50 -y 50 tight.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            int? xOffset = parseResult.GetValue(xOpt);
            int? yOffset = parseResult.GetValue(yOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Vignette (σ={sigma:F1}, x={xOffset}, y={yOffset})",
                img => ArtisticOps.Vignette(img, sigma, xOffset, yOffset));
        });
        return cmd;
    }

    public static Command CreateWaveCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var ampOpt = new Option<double>("-a", "--amplitude") { Description = "Wave amplitude in pixels (default 10)", DefaultValueFactory = _ => 10.0 };
        var wlOpt = new Option<double>("-w", "--wavelength") { Description = "Wave wavelength in pixels (default 50)", DefaultValueFactory = _ => 50.0 };
        var cmd = new Command("wave", """
            Applies a sine-wave distortion along the vertical axis.
            Image height is expanded to accommodate the wave displacement.

            Examples:
              sharpimage wave photo.jpg wavy.png
              sharpimage wave photo.jpg -a 20 -w 100 gentle_wave.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(ampOpt);
        cmd.Add(wlOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double amplitude = parseResult.GetValue(ampOpt);
            double wavelength = parseResult.GetValue(wlOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Wave (amp={amplitude:F1}, λ={wavelength:F1})",
                img => ArtisticOps.Wave(img, amplitude, wavelength));
        });
        return cmd;
    }

    public static Command CreateSwirlCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var degreesOpt = new Option<double>("-d", "--degrees") { Description = "Swirl rotation in degrees (default 60)", DefaultValueFactory = _ => 60.0 };
        var cmd = new Command("swirl", """
            Applies a rotational swirl distortion centered on the image.
            Pixels near the center rotate more than those near the edge.

            Examples:
              sharpimage swirl photo.jpg swirled.png
              sharpimage swirl photo.jpg -d 180 heavy_swirl.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(degreesOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double degrees = parseResult.GetValue(degreesOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Swirl ({degrees:F0}°)",
                img => ArtisticOps.Swirl(img, degrees));
        });
        return cmd;
    }

    public static Command CreateImplodeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var amountOpt = new Option<double>("-a", "--amount") { Description = "Distortion amount (positive=implode, negative=explode, default 0.5)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("implode", """
            Applies inward (implode) or outward (explode) spherical distortion.
            Positive amount pulls pixels toward center; negative pushes outward.

            Examples:
              sharpimage implode photo.jpg imploded.png
              sharpimage implode photo.jpg -a -0.5 exploded.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(amountOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double amount = parseResult.GetValue(amountOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Implode (amount={amount:F2})",
                img => ArtisticOps.Implode(img, amount));
        });
        return cmd;
    }
}
