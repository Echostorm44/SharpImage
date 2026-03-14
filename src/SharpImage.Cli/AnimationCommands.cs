using System.CommandLine;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Cli;

/// <summary>
/// CLI commands for GIF and APNG animation operations: create, split, info.
/// </summary>
public static class AnimationCommands
{
    public static Command CreateGifAnimCommand()
    {
        var inputArg = new Argument<string>("inputs") { Description = "Comma-separated input image paths for frames" };
        var outputArg = new Argument<string>("output") { Description = "Output animated GIF path" };
        var delayOption = new Option<int>("--delay") { Description = "Frame delay in centiseconds (default: 10)", DefaultValueFactory = _ => 10 };
        var loopOption = new Option<int>("--loop") { Description = "Loop count (0 = infinite, default: 0)", DefaultValueFactory = _ => 0 };

        var cmd = new Command("gifanim", "Create an animated GIF from multiple images.");
        cmd.Arguments.Add(inputArg);
        cmd.Arguments.Add(outputArg);
        cmd.Options.Add(delayOption);
        cmd.Options.Add(loopOption);

        cmd.SetAction(parseResult =>
        {
            string inputStr = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int delay = parseResult.GetValue(delayOption);
            int loop = parseResult.GetValue(loopOption);

            string[] inputPaths = inputStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (inputPaths.Length == 0)
            {
                Console.Error.WriteLine("No input files specified.");
                return;
            }

            var sequence = new ImageSequence { LoopCount = loop };

            foreach (string path in inputPaths)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"File not found: {path}");
                    return;
                }
                var frame = FormatRegistry.Read(path);
                frame.Delay = delay;
                sequence.AddFrame(frame);
            }

            GifCoder.WriteSequence(sequence, output);
            Console.WriteLine($"Created animated GIF: {output} ({sequence.Count} frames, delay={delay}cs, loop={loop})");
            sequence.Dispose();
        });

        return cmd;
    }

    public static Command CreateGifSplitCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated GIF path" };
        var outputArg = new Argument<string>("output") { Description = "Output path prefix (frames saved as prefix_0.png, prefix_1.png, ...)" };

        var cmd = new Command("gifsplit", "Split an animated GIF into individual frame images.");
        cmd.Arguments.Add(inputArg);
        cmd.Arguments.Add(outputArg);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string outputPrefix = parseResult.GetValue(outputArg)!;

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"File not found: {input}");
                return;
            }

            using var sequence = GifCoder.ReadSequence(input);
            Console.WriteLine($"Animated GIF: {sequence.Count} frames, canvas {sequence.CanvasWidth}x{sequence.CanvasHeight}, loop={sequence.LoopCount}");

            string dir = Path.GetDirectoryName(outputPrefix) ?? ".";
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            for (int i = 0; i < sequence.Count; i++)
            {
                string framePath = $"{outputPrefix}_{i}.png";
                FormatRegistry.Write(sequence[i], framePath);
                Console.WriteLine($"  Frame {i}: {sequence[i].Columns}x{sequence[i].Rows}, delay={sequence[i].Delay}cs → {framePath}");
            }
        });

        return cmd;
    }

    public static Command CreateGifInfoCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated GIF path" };

        var cmd = new Command("gifinfo", "Display information about an animated GIF.");
        cmd.Arguments.Add(inputArg);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"File not found: {input}");
                return;
            }

            using var sequence = GifCoder.ReadSequence(input);
            Console.WriteLine($"File:       {input}");
            Console.WriteLine($"Frames:     {sequence.Count}");
            Console.WriteLine($"Canvas:     {sequence.CanvasWidth}x{sequence.CanvasHeight}");
            Console.WriteLine($"Loop Count: {sequence.LoopCount} ({(sequence.LoopCount == 0 ? "infinite" : sequence.LoopCount + " times")})");
            Console.WriteLine($"Duration:   {sequence.TotalDuration}cs ({sequence.TotalDuration * 10}ms)");
            Console.WriteLine();

            for (int i = 0; i < sequence.Count; i++)
            {
                var frame = sequence[i];
                Console.WriteLine($"  Frame {i}: {frame.Columns}x{frame.Rows} delay={frame.Delay}cs dispose={frame.DisposeMethod} offset=({frame.Page.X},{frame.Page.Y})");
            }
        });

        return cmd;
    }

    public static Command CreateApngAnimCommand()
    {
        var inputArg = new Argument<string>("inputs") { Description = "Comma-separated input image paths for frames" };
        var outputArg = new Argument<string>("output") { Description = "Output animated PNG path" };
        var delayOption = new Option<int>("--delay") { Description = "Frame delay in milliseconds (default: 100)", DefaultValueFactory = _ => 100 };
        var loopOption = new Option<int>("--loop") { Description = "Loop count (0 = infinite, default: 0)", DefaultValueFactory = _ => 0 };

        var cmd = new Command("apnganim", "Create an animated PNG (APNG) from multiple images.");
        cmd.Arguments.Add(inputArg);
        cmd.Arguments.Add(outputArg);
        cmd.Options.Add(delayOption);
        cmd.Options.Add(loopOption);

        cmd.SetAction(parseResult =>
        {
            string inputStr = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int delay = parseResult.GetValue(delayOption);
            int loop = parseResult.GetValue(loopOption);

            string[] inputPaths = inputStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (inputPaths.Length == 0)
            {
                Console.Error.WriteLine("No input files specified.");
                return;
            }

            var frames = new List<ImageFrame>();
            foreach (string path in inputPaths)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"File not found: {path}");
                    return;
                }
                frames.Add(FormatRegistry.Read(path));
            }

            int[] delays = new int[frames.Count];
            Array.Fill(delays, delay);

            PngCoder.WriteSequence(frames.ToArray(), output, delays, loop);
            Console.WriteLine($"Created APNG: {output} ({frames.Count} frames, delay={delay}ms, loop={loop})");

            foreach (var f in frames) f.Dispose();
        });

        return cmd;
    }

    public static Command CreateApngSplitCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated PNG path" };
        var outputArg = new Argument<string>("output") { Description = "Output path prefix (frames saved as prefix_0.png, prefix_1.png, ...)" };

        var cmd = new Command("apngsplit", "Split an animated PNG into individual frame images.");
        cmd.Arguments.Add(inputArg);
        cmd.Arguments.Add(outputArg);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string outputPrefix = parseResult.GetValue(outputArg)!;

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"File not found: {input}");
                return;
            }

            var (frames, loopCount, delaysMs) = PngCoder.ReadSequence(input);
            Console.WriteLine($"APNG: {frames.Length} frames, loop={loopCount}");

            string dir = Path.GetDirectoryName(outputPrefix) ?? ".";
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            for (int i = 0; i < frames.Length; i++)
            {
                string framePath = $"{outputPrefix}_{i}.png";
                PngCoder.Write(frames[i], framePath);
                Console.WriteLine($"  Frame {i}: {frames[i].Columns}x{frames[i].Rows}, delay={delaysMs[i]}ms → {framePath}");
                frames[i].Dispose();
            }
        });

        return cmd;
    }

    public static Command CreateApngInfoCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated PNG path" };

        var cmd = new Command("apnginfo", "Display information about an animated PNG.");
        cmd.Arguments.Add(inputArg);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;

            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"File not found: {input}");
                return;
            }

            var (frames, loopCount, delaysMs) = PngCoder.ReadSequence(input);
            int totalDuration = delaysMs.Sum();
            Console.WriteLine($"File:       {input}");
            Console.WriteLine($"Frames:     {frames.Length}");
            Console.WriteLine($"Loop Count: {loopCount} ({(loopCount == 0 ? "infinite" : loopCount + " times")})");
            Console.WriteLine($"Duration:   {totalDuration}ms");
            Console.WriteLine();

            for (int i = 0; i < frames.Length; i++)
            {
                Console.WriteLine($"  Frame {i}: {frames[i].Columns}x{frames[i].Rows} delay={delaysMs[i]}ms");
                frames[i].Dispose();
            }
        });

        return cmd;
    }
}
