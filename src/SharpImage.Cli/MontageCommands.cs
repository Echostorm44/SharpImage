// SharpImage CLI — Montage commands: append, montage, coalesce.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;
using System.CommandLine;

namespace SharpImage.Cli;

public static class MontageCommands
{
    public static Command CreateAppendCommand()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "Two or more input image file paths", Arity = new ArgumentArity(2, 100) };
        var outputOption = new Option<string>("--output", "-o") { Description = "Output image file path", Required = true };
        var verticalOption = new Option<bool>("--vertical", "-v") { Description = "Stack images vertically instead of horizontally", DefaultValueFactory = _ => false };
        var cmd = new Command("append", """
            Combine multiple images side by side (horizontal) or stacked (vertical).
            Images are aligned by top edge (horizontal) or left edge (vertical).
            
            Examples:
              sharpimage append left.png right.png -o combined.png
              sharpimage append top.png bottom.png -v -o stacked.png
              sharpimage append a.png b.png c.png --vertical -o triptych.png
            """);
        cmd.Add(inputsArg);
        cmd.Add(outputOption);
        cmd.Add(verticalOption);
        cmd.SetAction((parseResult) =>
        {
            string[] inputs = parseResult.GetValue(inputsArg)!;
            string output = parseResult.GetValue(outputOption)!;
            bool vertical = parseResult.GetValue(verticalOption);

            if (inputs.Length < 2)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[red]Error: At least 2 input images required.[/]");
                return;
            }

            foreach (var inp in inputs)
            {
                if (!CliOutput.ValidateInputExists(inp)) return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var images = new ImageFrame[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                images[i] = FormatRegistry.Read(inputs[i]);

            var result = Montage.Append(images, horizontal: !vertical);
            FormatRegistry.Write(result, output);

            sw.Stop();
            CliOutput.PrintSuccess(inputs[0], output, sw.Elapsed);

            foreach (var img in images)
                img.Dispose();
            result.Dispose();
        });
        return cmd;
    }

    public static Command CreateMontageCommand()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "Input image file paths", Arity = new ArgumentArity(1, 100) };
        var outputOption = new Option<string>("--output", "-o") { Description = "Output image file path", Required = true };
        var columnsOption = new Option<int>("--columns", "-c") { Description = "Number of columns in the grid (auto if 0)", DefaultValueFactory = _ => 0 };
        var spacingOption = new Option<int>("--spacing", "-s") { Description = "Pixel spacing between tiles", DefaultValueFactory = _ => 4 };
        var bgOption = new Option<int>("--bg") { Description = "Background gray level 0-255 (default: 0 = black)", DefaultValueFactory = _ => 0 };
        var cmd = new Command("montage", """
            Tile multiple images in a grid layout.
            Images are centered within their cells. Auto-calculates grid dimensions if --columns not specified.
            
            Examples:
              sharpimage montage a.png b.png c.png d.png -o grid.png
              sharpimage montage a.png b.png c.png --columns 3 --spacing 8 -o gallery.png
              sharpimage montage a.png b.png c.png -c 2 -s 4 --bg 128 -o tiled.png
            """);
        cmd.Add(inputsArg);
        cmd.Add(outputOption);
        cmd.Add(columnsOption);
        cmd.Add(spacingOption);
        cmd.Add(bgOption);
        cmd.SetAction((parseResult) =>
        {
            string[] inputs = parseResult.GetValue(inputsArg)!;
            string output = parseResult.GetValue(outputOption)!;
            int columns = parseResult.GetValue(columnsOption);
            int spacing = parseResult.GetValue(spacingOption);
            int bg = parseResult.GetValue(bgOption);

            if (inputs.Length < 1)
            {
                Spectre.Console.AnsiConsole.MarkupLine("[red]Error: At least 1 input image required.[/]");
                return;
            }

            foreach (var inp in inputs)
            {
                if (!CliOutput.ValidateInputExists(inp)) return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var images = new ImageFrame[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                images[i] = FormatRegistry.Read(inputs[i]);

            ushort bgLevel = Quantum.ScaleFromByte((byte)Math.Clamp(bg, 0, 255));
            var result = Montage.Tile(images, columns, spacing, bgLevel);
            FormatRegistry.Write(result, output);

            sw.Stop();
            CliOutput.PrintSuccess(inputs[0], output, sw.Elapsed);

            foreach (var img in images)
                img.Dispose();
            result.Dispose();
        });
        return cmd;
    }

    public static Command CreateCoalesceCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated GIF or APNG file" };
        var outputArg = new Argument<string>("output") { Description = "Output directory for coalesced frames" };
        var formatOption = new Option<string>("--format") { Description = "Output format extension (default: png)", DefaultValueFactory = _ => "png" };
        var cmd = new Command("coalesce", """
            Flatten animation frames by compositing each onto the canvas.
            Produces full-canvas frames with disposal methods resolved.
            Useful for editing individual frames of an animation.
            
            Examples:
              sharpimage coalesce animation.gif frames/
              sharpimage coalesce animated.apng output/ --format bmp
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(formatOption);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string format = parseResult.GetValue(formatOption)!;

            if (!CliOutput.ValidateInputExists(input)) return;

            Directory.CreateDirectory(output);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Read the animation
            string ext = Path.GetExtension(input).ToLowerInvariant();
            ImageSequence? sequence = null;
            if (ext == ".gif")
            {
                using var stream = File.OpenRead(input);
                sequence = SharpImage.Formats.GifCoder.ReadSequence(stream);
            }
            else if (ext is ".png" or ".apng")
            {
                using var stream = File.OpenRead(input);
                var (frames, loopCount, delaysMs) = SharpImage.Formats.PngCoder.ReadSequence(stream);
                sequence = new ImageSequence { LoopCount = loopCount, FormatName = "APNG" };
                for (int i = 0; i < frames.Length; i++)
                {
                    frames[i].Delay = delaysMs[i] / 10; // Convert ms to centiseconds
                    sequence.AddFrame(frames[i]);
                }
            }
            else
            {
                Spectre.Console.AnsiConsole.MarkupLine("[red]Error: Coalesce only supports GIF and APNG input.[/]");
                return;
            }

            using var coalesced = Montage.Coalesce(sequence);
            sequence.Dispose();

            for (int i = 0; i < coalesced.Count; i++)
            {
                string framePath = Path.Combine(output, $"frame_{i:D4}.{format}");
                FormatRegistry.Write(coalesced[i], framePath);
            }

            sw.Stop();
            Spectre.Console.AnsiConsole.MarkupLine(
                $"[green]✓[/] Coalesced [cyan]{coalesced.Count}[/] frames to [cyan]{output}[/] in [yellow]{sw.Elapsed.TotalMilliseconds:F0}ms[/]");

            coalesced.Dispose();
        });
        return cmd;
    }
}
