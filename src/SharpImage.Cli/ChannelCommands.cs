// CLI commands for channel operations: separate, combine, swap.

using System.CommandLine;
using System.CommandLine.Parsing;
using Spectre.Console;
using SharpImage.Channel;
using SharpImage.Formats;

namespace SharpImage.Cli;

public static class ChannelCommands
{
    public static Command CreateSeparateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output base path (without extension) or single file path with --channel" };
        var channelOpt = new Option<string?>("--channel") { Description = "Extract a single channel: red, green, blue, alpha" };

        var cmd = new Command("separate", """
            Separate an image into individual channel images.
            Splits RGB into 3 grayscale images (Red, Green, Blue).
            Splits RGBA into 4 grayscale images (Red, Green, Blue, Alpha).
            
            Output files are named: <output>_red.png, <output>_green.png, etc.
            If --channel is specified, only that single channel is extracted.
            
            Examples:
              sharpimage separate photo.png channels/photo
              sharpimage separate photo.png red_only.png --channel red
            """);

        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(channelOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            string? channel = parseResult.GetValue(channelOpt);

            if (!CliOutput.ValidateInputExists(inPath)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            AnsiConsole.Progress()
                .AutoClear(false).HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask("[cyan]Reading[/]", maxValue: 100);
                    using var img = FormatRegistry.Read(inPath);
                    t1.Value = 100;

                    if (channel != null)
                    {
                        int idx = ParseChannelIndex(channel, img.HasAlpha);
                        var t2 = ctx.AddTask($"[cyan]Extracting {ChannelOps.GetChannelName(idx, img.HasAlpha)}[/]", maxValue: 100);
                        using var channelImg = ChannelOps.SeparateChannel(img, idx);
                        t2.Value = 100;

                        var t3 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                        string actualOut = EnsurePngExtension(outPath);
                        EnsureDirectoryFor(actualOut);
                        FormatRegistry.Write(channelImg, actualOut);
                        t3.Value = 100;

                        AnsiConsole.MarkupLine($"[bold cyan]Extracted:[/] {ChannelOps.GetChannelName(idx, img.HasAlpha)} → {actualOut}");
                    }
                    else
                    {
                        var t2 = ctx.AddTask("[cyan]Separating channels[/]", maxValue: 100);
                        var channels = ChannelOps.Separate(img);
                        t2.Value = 100;

                        var t3 = ctx.AddTask("[cyan]Writing channels[/]", maxValue: channels.Length);
                        string basePath = Path.Combine(
                            Path.GetDirectoryName(outPath) ?? ".",
                            Path.GetFileNameWithoutExtension(outPath));

                        for (int i = 0; i < channels.Length; i++)
                        {
                            string name = ChannelOps.GetChannelName(i, img.HasAlpha).ToLowerInvariant();
                            string filePath = $"{basePath}_{name}.png";
                            EnsureDirectoryFor(filePath);
                            FormatRegistry.Write(channels[i], filePath);
                            channels[i].Dispose();
                            t3.Increment(1);
                            AnsiConsole.MarkupLine($"  [green]✓[/] {name} → {filePath}");
                        }
                    }
                });

            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });

        return cmd;
    }

    public static Command CreateCombineCommand()
    {
        var inputsArg = new Argument<string[]>("inputs") 
        { 
            Description = "Channel image paths (3 for RGB, 4 for RGBA) followed by output path",
            Arity = new ArgumentArity(4, 5)
        };

        var cmd = new Command("combine", """
            Combine separate channel images into a single composite image.
            Accepts 3 images (RGB) or 4 images (RGBA).
            Each input should be a grayscale image representing one channel.
            
            Examples:
              sharpimage combine red.png green.png blue.png output.png
              sharpimage combine red.png green.png blue.png alpha.png output.png
            """);

        cmd.Add(inputsArg);

        cmd.SetAction((parseResult) =>
        {
            string[] paths = parseResult.GetValue(inputsArg)!;
            if (paths.Length < 4 || paths.Length > 5)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Provide 3 or 4 channel images + 1 output path");
                return;
            }

            int channelCount = paths.Length - 1;
            string outPath = paths[^1];
            string[] inputPaths = paths[..channelCount];

            foreach (var p in inputPaths)
            {
                if (!CliOutput.ValidateInputExists(p)) return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            AnsiConsole.Progress()
                .AutoClear(false).HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask("[cyan]Reading channels[/]", maxValue: channelCount);
                    var channelImages = new SharpImage.Image.ImageFrame[channelCount];
                    for (int i = 0; i < channelCount; i++)
                    {
                        channelImages[i] = FormatRegistry.Read(inputPaths[i]);
                        t1.Increment(1);
                    }

                    var t2 = ctx.AddTask("[cyan]Combining[/]", maxValue: 100);
                    using var result = ChannelOps.Combine(channelImages);
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    EnsureDirectoryFor(outPath);
                    FormatRegistry.Write(result, outPath);
                    t3.Value = 100;

                    foreach (var ch in channelImages) ch.Dispose();
                });

            AnsiConsole.MarkupLine($"[bold cyan]Combined {channelCount} channels[/]");
            CliOutput.PrintSuccess(inputPaths[0], outPath, sw.Elapsed);
        });

        return cmd;
    }

    public static Command CreateSwapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };
        var fromOpt = new Option<string>("--from") { Description = "First channel to swap", Required = true };
        var toOpt = new Option<string>("--to") { Description = "Second channel to swap", Required = true };

        var cmd = new Command("swapchannel", """
            Swap two channels in an image.
            
            Channel names: red (0), green (1), blue (2), alpha (3)
            
            Examples:
              sharpimage swapchannel photo.png output.png --from red --to blue
            """);

        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(fromOpt);
        cmd.Add(toOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            string from = parseResult.GetValue(fromOpt)!;
            string to = parseResult.GetValue(toOpt)!;

            if (!CliOutput.ValidateInputExists(inPath)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            AnsiConsole.Progress()
                .AutoClear(false).HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask("[cyan]Reading[/]", maxValue: 100);
                    using var img = FormatRegistry.Read(inPath);
                    t1.Value = 100;

                    int fromIdx = ParseChannelIndex(from, img.HasAlpha);
                    int toIdx = ParseChannelIndex(to, img.HasAlpha);

                    var t2 = ctx.AddTask($"[cyan]Swapping {from} ↔ {to}[/]", maxValue: 100);
                    ChannelOps.SwapChannels(img, fromIdx, toIdx);
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    EnsureDirectoryFor(outPath);
                    FormatRegistry.Write(img, outPath);
                    t3.Value = 100;
                });

            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });

        return cmd;
    }

    private static int ParseChannelIndex(string name, bool hasAlpha)
    {
        return name.ToLowerInvariant() switch
        {
            "red" or "r" or "0" => 0,
            "green" or "g" or "1" => 1,
            "blue" or "b" or "2" => 2,
            "alpha" or "a" or "3" when hasAlpha => 3,
            "alpha" or "a" or "3" => throw new ArgumentException("Image does not have an alpha channel"),
            _ => throw new ArgumentException($"Unknown channel: {name}. Use red, green, blue, or alpha.")
        };
    }

    private static string EnsurePngExtension(string path)
    {
        if (!Path.HasExtension(path)) return path + ".png";
        return path;
    }

    private static void EnsureDirectoryFor(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    public static Command CreateAlphaExtractCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output grayscale image path" };

        var cmd = new Command("alpha-extract", """
            Extract the alpha channel as a grayscale image.
            White = fully opaque, Black = fully transparent.

            Examples:
              sharpimage alpha-extract icon.png alpha_mask.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(inPath)) return;

            CliOutput.RunPipeline(inPath, outPath, "Extracting alpha", img =>
                AlphaOps.Extract(img));
        });
        return cmd;
    }

    public static Command CreateAlphaRemoveCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output opaque image path" };
        var bgOpt = new Option<string>("--background") { Description = "Background color as hex (default: white)", DefaultValueFactory = _ => "FFFFFF" };

        var cmd = new Command("alpha-remove", """
            Remove the alpha channel by compositing over a background color.
            Default background is white.

            Examples:
              sharpimage alpha-remove logo.png opaque.png
              sharpimage alpha-remove logo.png on_black.png --background 000000
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(bgOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            string bgHex = parseResult.GetValue(bgOpt)!;
            if (!CliOutput.ValidateInputExists(inPath)) return;

            var (bgR, bgG, bgB) = ParseHexColor16(bgHex);
            CliOutput.RunPipeline(inPath, outPath, "Removing alpha", img =>
                AlphaOps.Remove(img, bgR, bgG, bgB));
        });
        return cmd;
    }

    public static Command CreateAlphaSetCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };
        var valueOpt = new Option<int>("--value") { Description = "Alpha value 0-65535 (default: 65535 = fully opaque)", DefaultValueFactory = _ => 65535 };

        var cmd = new Command("alpha-set", """
            Set all pixels to a specific alpha value.
            If the image has no alpha channel, one is added.

            Examples:
              sharpimage alpha-set photo.png semitransparent.png --value 32768
              sharpimage alpha-set photo.png opaque.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(valueOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            int val = parseResult.GetValue(valueOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            CliOutput.RunPipeline(inPath, outPath, "Setting alpha", img =>
                AlphaOps.SetAlpha(img, (ushort)Math.Clamp(val, 0, 65535)));
        });
        return cmd;
    }

    public static Command CreateAlphaOpaqueCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };

        var cmd = new Command("alpha-opaque", """
            Make all pixels fully opaque (alpha = max).

            Examples:
              sharpimage alpha-opaque transparent.png opaque.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(inPath)) return;

            CliOutput.RunPipeline(inPath, outPath, "Making opaque", img =>
                AlphaOps.MakeOpaque(img));
        });
        return cmd;
    }

    public static Command CreateAlphaTransparentCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };
        var colorOpt = new Option<string>("--color") { Description = "Target color as hex to make transparent", Required = true };
        var fuzzOpt = new Option<double>("--fuzz") { Description = "Color tolerance 0.0-1.0 (default: 0.05)", DefaultValueFactory = _ => 0.05 };

        var cmd = new Command("alpha-transparent", """
            Make pixels matching a target color transparent.
            Useful for removing solid backgrounds.

            Examples:
              sharpimage alpha-transparent logo.png cutout.png --color FFFFFF --fuzz 0.1
              sharpimage alpha-transparent photo.png result.png --color 00FF00
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(colorOpt);
        cmd.Add(fuzzOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            string colorHex = parseResult.GetValue(colorOpt)!;
            double fuzz = parseResult.GetValue(fuzzOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            var (tR, tG, tB) = ParseHexColor16(colorHex);
            CliOutput.RunPipeline(inPath, outPath, "Making transparent", img =>
                AlphaOps.MakeTransparent(img, tR, tG, tB, fuzz));
        });
        return cmd;
    }

    private static (ushort R, ushort G, ushort B) ParseHexColor16(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new ArgumentException($"Invalid hex color: {hex}. Expected 6 hex digits.");
        int r8 = Convert.ToInt32(hex[..2], 16);
        int g8 = Convert.ToInt32(hex[2..4], 16);
        int b8 = Convert.ToInt32(hex[4..6], 16);
        return ((ushort)(r8 * 257), (ushort)(g8 * 257), (ushort)(b8 * 257));
    }
}
