// CLI commands for procedural image generation.

using SharpImage.Formats;
using SharpImage.Generators;
using SharpImage.Image;
using Spectre.Console;
using System.CommandLine;
using System.Diagnostics;

namespace SharpImage.Cli;

public static class GenerateCommands
{
    public static Command CreateGradientCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var widthOpt = new Option<int>("--width") { Description = "Image width", DefaultValueFactory = _ => 256 };
        var heightOpt = new Option<int>("--height") { Description = "Image height", DefaultValueFactory = _ => 256 };
        var startOpt = new Option<string>("--start-color") { Description = "Start color (R,G,B)", DefaultValueFactory = _ => "255,0,0" };
        var endOpt = new Option<string>("--end-color") { Description = "End color (R,G,B)", DefaultValueFactory = _ => "0,0,255" };
        var typeOpt = new Option<string>("--type") { Description = "Gradient type: linear or radial", DefaultValueFactory = _ => "linear" };
        var angleOpt = new Option<double>("--angle") { Description = "Angle in degrees (linear only, 0=left-to-right, 90=top-to-bottom)", DefaultValueFactory = _ => 0.0 };

        var cmd = new Command("gradient", "Generate a gradient image")
        {
            outputArg
        };
        cmd.Options.Add(widthOpt);
        cmd.Options.Add(heightOpt);
        cmd.Options.Add(startOpt);
        cmd.Options.Add(endOpt);
        cmd.Options.Add(typeOpt);
        cmd.Options.Add(angleOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            var (sr, sg, sb) = ParseColor(parseResult.GetValue(startOpt)!);
            var (er, eg, eb) = ParseColor(parseResult.GetValue(endOpt)!);
            string type = parseResult.GetValue(typeOpt)!;
            double angle = parseResult.GetValue(angleOpt);

            var sw = Stopwatch.StartNew();
            ImageFrame frame;

            if (type.Equals("radial", StringComparison.OrdinalIgnoreCase))
                frame = GradientGenerator.Radial(w, h, sr, sg, sb, er, eg, eb);
            else
                frame = GradientGenerator.Linear(w, h, sr, sg, sb, er, eg, eb, angle);

            FormatRegistry.Write(frame, output);
            sw.Stop();

            AnsiConsole.MarkupLine($"[green]✓[/] Generated {type} gradient {w}×{h} → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    public static Command CreatePlasmaCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var widthOpt = new Option<int>("--width") { Description = "Image width", DefaultValueFactory = _ => 256 };
        var heightOpt = new Option<int>("--height") { Description = "Image height", DefaultValueFactory = _ => 256 };
        var seedOpt = new Option<int?>("--seed") { Description = "Random seed for reproducibility" };

        var cmd = new Command("plasma", "Generate a plasma fractal image")
        {
            outputArg
        };
        cmd.Options.Add(widthOpt);
        cmd.Options.Add(heightOpt);
        cmd.Options.Add(seedOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            int? seed = parseResult.GetValue(seedOpt);

            var sw = Stopwatch.StartNew();
            var frame = PlasmaGenerator.Generate(w, h, seed);
            FormatRegistry.Write(frame, output);
            sw.Stop();

            string seedStr = seed.HasValue ? $", seed={seed}" : "";
            AnsiConsole.MarkupLine($"[green]✓[/] Generated plasma {w}×{h}{seedStr} → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    public static Command CreatePatternCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var nameOpt = new Option<string>("--name") { Description = "Pattern name", DefaultValueFactory = _ => "Checkerboard" };
        var widthOpt = new Option<int>("--width") { Description = "Image width", DefaultValueFactory = _ => 256 };
        var heightOpt = new Option<int>("--height") { Description = "Image height", DefaultValueFactory = _ => 256 };
        var fgOpt = new Option<string>("--fg") { Description = "Foreground color (R,G,B)", DefaultValueFactory = _ => "0,0,0" };
        var bgOpt = new Option<string>("--bg") { Description = "Background color (R,G,B)", DefaultValueFactory = _ => "255,255,255" };

        var cmd = new Command("pattern", "Generate a tiled pattern image")
        {
            outputArg
        };
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(widthOpt);
        cmd.Options.Add(heightOpt);
        cmd.Options.Add(fgOpt);
        cmd.Options.Add(bgOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string name = parseResult.GetValue(nameOpt)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            var (fr, fg, fb) = ParseColor(parseResult.GetValue(fgOpt)!);
            var (br, bg, bb) = ParseColor(parseResult.GetValue(bgOpt)!);

            if (!Enum.TryParse<PatternName>(name, ignoreCase: true, out var patternName))
            {
                AnsiConsole.MarkupLine($"[red]Unknown pattern: {name}[/]");
                AnsiConsole.MarkupLine("[yellow]Available patterns:[/]");
                foreach (var p in PatternGenerator.GetAllPatternNames())
                    AnsiConsole.MarkupLine($"  {p}");
                return;
            }

            var sw = Stopwatch.StartNew();
            var frame = PatternGenerator.Generate(w, h, patternName, fr, fg, fb, br, bg, bb);
            FormatRegistry.Write(frame, output);
            sw.Stop();

            AnsiConsole.MarkupLine($"[green]✓[/] Generated {name} pattern {w}×{h} → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    public static Command CreateLabelCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var textOpt = new Option<string>("--text") { Description = "Text to render", DefaultValueFactory = _ => "Hello" };
        var colorOpt = new Option<string>("--color") { Description = "Text color (R,G,B)", DefaultValueFactory = _ => "0,0,0" };
        var bgOpt = new Option<string>("--background") { Description = "Background color (R,G,B)", DefaultValueFactory = _ => "255,255,255" };
        var scaleOpt = new Option<double>("--scale") { Description = "Font scale factor", DefaultValueFactory = _ => 2.0 };
        var widthOpt = new Option<int?>("--width") { Description = "Max width for caption wrapping (omit for auto-sized label)" };

        var cmd = new Command("label", "Generate a text label or caption image")
        {
            outputArg
        };
        cmd.Options.Add(textOpt);
        cmd.Options.Add(colorOpt);
        cmd.Options.Add(bgOpt);
        cmd.Options.Add(scaleOpt);
        cmd.Options.Add(widthOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string text = parseResult.GetValue(textOpt)!;
            var (tr, tg, tb) = ParseColor(parseResult.GetValue(colorOpt)!);
            var (br, bg, bb) = ParseColor(parseResult.GetValue(bgOpt)!);
            double scale = parseResult.GetValue(scaleOpt);
            int? maxWidth = parseResult.GetValue(widthOpt);

            var sw = Stopwatch.StartNew();
            ImageFrame frame;

            if (maxWidth.HasValue)
                frame = LabelGenerator.Caption(text, maxWidth.Value, tr, tg, tb, br, bg, bb, scale);
            else
                frame = LabelGenerator.Label(text, tr, tg, tb, br, bg, bb, scale);

            FormatRegistry.Write(frame, output);
            sw.Stop();

            string mode = maxWidth.HasValue ? "caption" : "label";
            AnsiConsole.MarkupLine($"[green]✓[/] Generated {mode} \"{text}\" → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    public static Command CreateSolidCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var widthOpt = new Option<int>("--width") { Description = "Image width", DefaultValueFactory = _ => 256 };
        var heightOpt = new Option<int>("--height") { Description = "Image height", DefaultValueFactory = _ => 256 };
        var colorOpt = new Option<string>("--color") { Description = "Fill color (R,G,B or R,G,B,A)", DefaultValueFactory = _ => "128,128,128" };

        var cmd = new Command("solid", "Generate a solid color image")
        {
            outputArg
        };
        cmd.Options.Add(widthOpt);
        cmd.Options.Add(heightOpt);
        cmd.Options.Add(colorOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            var (r, g, b, a) = ParseColorWithAlpha(parseResult.GetValue(colorOpt)!);

            var sw = Stopwatch.StartNew();
            var frame = SolidGenerator.Generate(w, h, r, g, b, a);
            FormatRegistry.Write(frame, output);
            sw.Stop();

            AnsiConsole.MarkupLine($"[green]✓[/] Generated solid {w}×{h} ({r},{g},{b}) → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    public static Command CreateNoiseCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var typeOpt = new Option<string>("--type") { Description = "Noise type: perlin, simplex, worley, fbm, turbulence, marble, wood", DefaultValueFactory = _ => "perlin" };
        var widthOpt = new Option<int>("--width") { Description = "Image width", DefaultValueFactory = _ => 256 };
        var heightOpt = new Option<int>("--height") { Description = "Image height", DefaultValueFactory = _ => 256 };
        var freqOpt = new Option<double>("--frequency") { Description = "Noise frequency (default 4.0)", DefaultValueFactory = _ => 4.0 };
        var seedOpt = new Option<int>("--seed") { Description = "Random seed", DefaultValueFactory = _ => 0 };
        var octavesOpt = new Option<int>("--octaves") { Description = "FBM octaves (default 6)", DefaultValueFactory = _ => 6 };
        var colorizeOpt = new Option<bool>("--colorize") { Description = "Generate color noise instead of grayscale", DefaultValueFactory = _ => false };

        var cmd = new Command("noisegen", "Generate a procedural noise image (Perlin, Simplex, Worley, FBM, Turbulence, Marble, Wood)")
        {
            outputArg
        };
        cmd.Options.Add(typeOpt);
        cmd.Options.Add(widthOpt);
        cmd.Options.Add(heightOpt);
        cmd.Options.Add(freqOpt);
        cmd.Options.Add(seedOpt);
        cmd.Options.Add(octavesOpt);
        cmd.Options.Add(colorizeOpt);

        cmd.SetAction(parseResult =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string type = parseResult.GetValue(typeOpt)!.ToLowerInvariant();
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            double freq = parseResult.GetValue(freqOpt);
            int seed = parseResult.GetValue(seedOpt);
            int octaves = parseResult.GetValue(octavesOpt);
            bool colorize = parseResult.GetValue(colorizeOpt);

            var sw = Stopwatch.StartNew();
            ImageFrame frame = type switch
            {
                "simplex" => NoiseGenerator.Simplex(w, h, freq, seed, colorize),
                "worley" => NoiseGenerator.Worley(w, h, freq, seed),
                "fbm" => NoiseGenerator.Fbm(w, h, octaves, frequency: freq, seed: seed, colorize: colorize),
                "turbulence" => NoiseGenerator.Turbulence(w, h, octaves, frequency: freq, seed: seed, colorize: colorize),
                "marble" => NoiseGenerator.Marble(w, h, freq, seed: seed),
                "wood" => NoiseGenerator.Wood(w, h, frequency: freq, seed: seed),
                _ => NoiseGenerator.Perlin(w, h, freq, seed, colorize),
            };

            FormatRegistry.Write(frame, output);
            sw.Stop();

            AnsiConsole.MarkupLine($"[green]✓[/] Generated {type} noise {w}×{h} → [cyan]{output}[/] ({sw.ElapsedMilliseconds}ms)");
        });

        return cmd;
    }

    private static (byte R, byte G, byte B) ParseColor(string color)
    {
        string[] parts = color.Split(',');
        if (parts.Length < 3) return (128, 128, 128);
        return (byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
    }

    private static (byte R, byte G, byte B, byte A) ParseColorWithAlpha(string color)
    {
        string[] parts = color.Split(',');
        if (parts.Length < 3) return (128, 128, 128, 255);
        byte a = parts.Length >= 4 ? byte.Parse(parts[3].Trim()) : (byte)255;
        return (byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()), a);
    }
}
