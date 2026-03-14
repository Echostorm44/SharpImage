// SharpImage CLI — Math operations: evaluate, statistic, function, polynomial.

using System.CommandLine;
using SharpImage.Effects;
using SharpImage.Formats;

namespace SharpImage.Cli;

public static class MathCommands
{
    public static Command CreateEvaluateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var opOpt = new Option<string>("-op", "--operator") { Description = "Operation: add, subtract, multiply, divide, min, max, set, pow, log, threshold, and, or, xor", DefaultValueFactory = _ => "add" };
        var valueOpt = new Option<double>("-v", "--value") { Description = "Constant value for the operation", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("evaluate", """
            Apply per-pixel arithmetic with a constant value.
            Every pixel channel is modified: result = op(pixel, value).

            Examples:
              sharpimage evaluate photo.jpg -op multiply -v 1.5 brighter.png
              sharpimage evaluate photo.jpg -op threshold -v 32768 binary.png
              sharpimage evaluate photo.jpg -op pow -v 0.5 gamma.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(opOpt);
        cmd.Add(valueOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string opName = parseResult.GetValue(opOpt)!;
            double value = parseResult.GetValue(valueOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            if (!TryParseEvaluateOperator(opName, out var op))
            {
                CliOutput.PrintError($"Unknown operator: {opName}");
                return;
            }

            CliOutput.RunPipeline(input, output, $"Evaluate ({opName}, value={value})",
                img => MathOps.Evaluate(img, op, value));
        });
        return cmd;
    }

    public static Command CreateStatisticCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var typeOpt = new Option<string>("-t", "--type") { Description = "Statistic: minimum, maximum, mean, median, mode, rms, stddev, nonpeak, gradient, contrast", DefaultValueFactory = _ => "median" };
        var radiusOpt = new Option<int>("-r", "--radius") { Description = "Window radius (window = 2r+1)", DefaultValueFactory = _ => 1 };
        var cmd = new Command("statistic", """
            Apply a statistical function in a neighborhood around each pixel.
            Useful for noise reduction (median), edge emphasis (gradient), and more.

            Examples:
              sharpimage statistic photo.jpg -t median -r 2 denoised.png
              sharpimage statistic photo.jpg -t gradient -r 1 edges.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(typeOpt);
        cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string typeName = parseResult.GetValue(typeOpt)!;
            int radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            if (!TryParseStatisticType(typeName, out var type))
            {
                CliOutput.PrintError($"Unknown statistic type: {typeName}");
                return;
            }

            CliOutput.RunPipeline(input, output, $"Statistic ({typeName}, radius={radius})",
                img => MathOps.Statistic(img, type, radius));
        });
        return cmd;
    }

    public static Command CreateFunctionCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var funcOpt = new Option<string>("-f", "--function") { Description = "Function: polynomial, sinusoid, arcsin, arctan", DefaultValueFactory = _ => "polynomial" };
        var paramsOpt = new Option<string>("-p", "--params") { Description = "Comma-separated function parameters" };
        var cmd = new Command("function", """
            Apply a mathematical function to every pixel.
            Polynomial: coefficients c0,c1,...cn for c0*x^n + c1*x^(n-1) + ... + cn
            Sinusoid: amplitude,frequency,phase,bias
            Arcsin/Arctan: width,slope,center,bias

            Examples:
              sharpimage function photo.jpg -f polynomial -p 2,-1,0.5 adjusted.png
              sharpimage function photo.jpg -f sinusoid -p 0.5,3,0,0.5 wave.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(funcOpt);
        cmd.Add(paramsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string funcName = parseResult.GetValue(funcOpt)!;
            string? paramsStr = parseResult.GetValue(paramsOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            if (!TryParseMathFunction(funcName, out var func))
            {
                CliOutput.PrintError($"Unknown function: {funcName}");
                return;
            }

            double[] parameters;
            if (string.IsNullOrWhiteSpace(paramsStr))
                parameters = func == MathFunction.Polynomial ? [1.0, 0.0] : [1.0, 1.0, 0.0, 0.5];
            else
                parameters = paramsStr.Split(',').Select(s => double.Parse(s.Trim())).ToArray();

            CliOutput.RunPipeline(input, output, $"Function ({funcName})",
                img => MathOps.ApplyFunction(img, func, parameters));
        });
        return cmd;
    }

    public static Command CreatePolynomialCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var inputsOpt = new Option<string>("-i", "--inputs") { Description = "Comma-separated input image paths" };
        var termsOpt = new Option<string>("-t", "--terms") { Description = "Comma-separated weight,exponent pairs (e.g. 0.7,1.0,0.3,1.0)" };
        var cmd = new Command("polynomial", """
            Weighted polynomial combination of multiple images.
            Terms are weight,exponent pairs applied to each corresponding input image.
            Result = sum(weight_i * image_i ^ exponent_i) per pixel.

            Examples:
              sharpimage polynomial -i img1.png,img2.png -t 0.7,1.0,0.3,1.0 blended.png
            """);
        cmd.Add(outputArg);
        cmd.Add(inputsOpt);
        cmd.Add(termsOpt);
        cmd.SetAction((parseResult) =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string? inputsStr = parseResult.GetValue(inputsOpt);
            string? termsStr = parseResult.GetValue(termsOpt);

            if (string.IsNullOrWhiteSpace(inputsStr) || string.IsNullOrWhiteSpace(termsStr))
            {
                CliOutput.PrintError("Both --inputs and --terms are required.");
                return;
            }

            string[] inputPaths = inputsStr.Split(',').Select(s => s.Trim()).ToArray();
            double[] terms = termsStr.Split(',').Select(s => double.Parse(s.Trim())).ToArray();

            foreach (var p in inputPaths)
            {
                if (!CliOutput.ValidateInputExists(p)) return;
            }

            var images = inputPaths.Select(p => FormatRegistry.Read(p)).ToArray();
            try
            {
                using var result = MathOps.Polynomial(images, terms);
                FormatRegistry.Write(result, output);
                CliOutput.PrintSuccess(inputPaths[0], output, TimeSpan.Zero);
            }
            finally
            {
                foreach (var img in images) img.Dispose();
            }
        });
        return cmd;
    }

    private static bool TryParseEvaluateOperator(string name, out EvaluateOperator op)
    {
        op = name.ToLowerInvariant() switch
        {
            "add" => EvaluateOperator.Add,
            "subtract" or "sub" => EvaluateOperator.Subtract,
            "multiply" or "mul" => EvaluateOperator.Multiply,
            "divide" or "div" => EvaluateOperator.Divide,
            "abs" => EvaluateOperator.Abs,
            "min" => EvaluateOperator.Min,
            "max" => EvaluateOperator.Max,
            "set" => EvaluateOperator.Set,
            "and" => EvaluateOperator.And,
            "or" => EvaluateOperator.Or,
            "xor" => EvaluateOperator.Xor,
            "leftshift" or "lshift" => EvaluateOperator.LeftShift,
            "rightshift" or "rshift" => EvaluateOperator.RightShift,
            "log" => EvaluateOperator.Log,
            "pow" => EvaluateOperator.Pow,
            "cosine" or "cos" => EvaluateOperator.Cosine,
            "sine" or "sin" => EvaluateOperator.Sine,
            "exponential" or "exp" => EvaluateOperator.Exponential,
            "thresholdblack" => EvaluateOperator.ThresholdBlack,
            "thresholdwhite" => EvaluateOperator.ThresholdWhite,
            "threshold" => EvaluateOperator.Threshold,
            "addmodulus" => EvaluateOperator.AddModulus,
            "inverselog" => EvaluateOperator.InverseLog,
            "mean" => EvaluateOperator.Mean,
            _ => EvaluateOperator.Add,
        };
        return name.ToLowerInvariant() is "add" or "subtract" or "sub" or "multiply" or "mul"
            or "divide" or "div" or "abs" or "min" or "max" or "set" or "and" or "or" or "xor"
            or "leftshift" or "lshift" or "rightshift" or "rshift" or "log" or "pow"
            or "cosine" or "cos" or "sine" or "sin" or "exponential" or "exp"
            or "thresholdblack" or "thresholdwhite" or "threshold" or "addmodulus" or "inverselog" or "mean";
    }

    private static bool TryParseStatisticType(string name, out StatisticType type)
    {
        type = name.ToLowerInvariant() switch
        {
            "minimum" or "min" => StatisticType.Minimum,
            "maximum" or "max" => StatisticType.Maximum,
            "mean" => StatisticType.Mean,
            "median" => StatisticType.Median,
            "mode" => StatisticType.Mode,
            "rms" or "rootmeansquare" => StatisticType.RootMeanSquare,
            "stddev" or "standarddeviation" => StatisticType.StandardDeviation,
            "nonpeak" => StatisticType.Nonpeak,
            "gradient" => StatisticType.Gradient,
            "contrast" => StatisticType.Contrast,
            _ => StatisticType.Median,
        };
        return name.ToLowerInvariant() is "minimum" or "min" or "maximum" or "max" or "mean"
            or "median" or "mode" or "rms" or "rootmeansquare" or "stddev" or "standarddeviation"
            or "nonpeak" or "gradient" or "contrast";
    }

    private static bool TryParseMathFunction(string name, out MathFunction func)
    {
        func = name.ToLowerInvariant() switch
        {
            "polynomial" or "poly" => MathFunction.Polynomial,
            "sinusoid" or "sin" => MathFunction.Sinusoid,
            "arcsin" or "asin" => MathFunction.Arcsin,
            "arctan" or "atan" => MathFunction.Arctan,
            _ => MathFunction.Polynomial,
        };
        return name.ToLowerInvariant() is "polynomial" or "poly" or "sinusoid" or "sin"
            or "arcsin" or "asin" or "arctan" or "atan";
    }
}
