// SharpImage CLI — FX expression evaluator command.

using SharpImage.Fx;
using System.CommandLine;

namespace SharpImage.Cli;

public static class FxCommands
{
    public static Command CreateFxCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var exprArg = new Argument<string>("expression") { Description = "FX expression to evaluate per-pixel" };

        var cmd = new Command("fx", """
            Per-pixel math expression evaluator (like ImageMagick -fx).
            Evaluates an expression for every pixel; 'u' = source pixel (0-1).
            
            Variables: u (pixel), i/j (coords), w/h (dimensions)
            Channels:  u.r, u.g, u.b, u.a
            Functions: sin, cos, abs, sqrt, pow, min, max, clamp, if, ...
            Constants: pi, e, phi, quantumrange
            
            Examples:
              sharpimage fx input.jpg "1.0 - u" negated.png
              sharpimage fx input.jpg "u ^ 0.4545" gamma.png
              sharpimage fx input.jpg "u > 0.5 ? 1 : 0" threshold.png
              sharpimage fx input.jpg "floor(u * 4) / 4" posterize.png
              sharpimage fx input.jpg "sin(i * pi / w) * u" wave.png
            """);
        cmd.Add(inputArg);
        cmd.Add(exprArg);
        cmd.Add(outputArg);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string expr = parseResult.GetValue(exprArg)!;

            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output,
                $"FX: {(expr.Length > 40 ? expr[..37] + "..." : expr)}",
                img => FxEvaluator.Apply(img, expr));
        });
        return cmd;
    }
}
