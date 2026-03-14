// SharpImage CLI — Spectre.Console output helpers.
// Provides progress bars, tables, and formatted output for all commands.

using SharpImage.Formats;
using SharpImage.Image;
using Spectre.Console;
using System.Diagnostics;

namespace SharpImage.Cli;

public static class CliOutput
{
    public static void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("SharpImage").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Pure C# .NET 10 Image Processing — 33 formats, zero native dependencies[/]");
        AnsiConsole.WriteLine();
    }

    public static ImageFrame ReadImageWithProgress(string path)
    {
        ImageFrame? result = null;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start($"Reading [cyan]{Path.GetFileName(path)}[/]...", ctx =>
            {
                result = FormatRegistry.Read(path);
            });
        return result!;
    }

    public static void WriteImageWithProgress(ImageFrame image, string path)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("green"))
            .Start($"Writing [green]{Path.GetFileName(path)}[/]...", ctx =>
            {
                FormatRegistry.Write(image, path);
            });
    }

    /// <summary>
    /// Read → Process → Write pipeline with progress reporting.
    /// </summary>
    public static void RunPipeline(string inputPath, string outputPath, string operationName,
        Func<ImageFrame, ImageFrame> process, int quality = 100)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    var readTask = ctx.AddTask($"[cyan]Reading[/] {Path.GetFileName(inputPath)}", maxValue: 100);
                    var image = FormatRegistry.Read(inputPath);
                    readTask.Value = 100;

                    var processTask = ctx.AddTask($"[yellow]{operationName}[/]", maxValue: 100);
                    var result = process(image);
                    processTask.Value = 100;

                    var writeTask = ctx.AddTask($"[green]Writing[/] {Path.GetFileName(outputPath)}", maxValue: 100);
                    FormatRegistry.Write(result, outputPath, quality);
                    writeTask.Value = 100;
                });

            sw.Stop();
            PrintSuccess(inputPath, outputPath, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            PrintError(ex.Message);
        }
    }

    /// <summary>
    /// Read → Process → Custom Write pipeline (for format-specific encoding like BC7).
    /// </summary>
    public static void RunPipelineCustomWrite(string inputPath, string outputPath, string operationName,
        Func<ImageFrame, ImageFrame> process, Func<ImageFrame, byte[]> encode)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    var readTask = ctx.AddTask($"[cyan]Reading[/] {Path.GetFileName(inputPath)}", maxValue: 100);
                    var image = FormatRegistry.Read(inputPath);
                    readTask.Value = 100;

                    var processTask = ctx.AddTask($"[yellow]{operationName}[/]", maxValue: 100);
                    var result = process(image);
                    processTask.Value = 100;

                    var writeTask = ctx.AddTask($"[green]Writing[/] {Path.GetFileName(outputPath)}", maxValue: 100);
                    byte[] data = encode(result);
                    File.WriteAllBytes(outputPath, data);
                    writeTask.Value = 100;
                });

            sw.Stop();
            PrintSuccess(inputPath, outputPath, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            PrintError(ex.Message);
        }
    }

    /// <summary>
    /// Read → Process (in-place mutation) → Write pipeline.
    /// </summary>
    public static void RunMutatingPipeline(string inputPath, string outputPath, string operationName,
        Action<ImageFrame> process)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    var readTask = ctx.AddTask($"[cyan]Reading[/] {Path.GetFileName(inputPath)}", maxValue: 100);
                    var image = FormatRegistry.Read(inputPath);
                    readTask.Value = 100;

                    var processTask = ctx.AddTask($"[yellow]{operationName}[/]", maxValue: 100);
                    process(image);
                    processTask.Value = 100;

                    var writeTask = ctx.AddTask($"[green]Writing[/] {Path.GetFileName(outputPath)}", maxValue: 100);
                    FormatRegistry.Write(image, outputPath);
                    writeTask.Value = 100;
                });

            sw.Stop();
            PrintSuccess(inputPath, outputPath, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            PrintError(ex.Message);
        }
    }

    public static void PrintSuccess(string input, string output, TimeSpan elapsed)
    {
        AnsiConsole.MarkupLine($"\n[green]✓[/] [bold]{Path.GetFileName(input)}[/] → [bold]{Path.GetFileName(output)}[/] in [cyan]{elapsed.TotalMilliseconds:F0}ms[/]");
    }

    public static void PrintError(string message)
    {
        AnsiConsole.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(message)}");
    }

    public static bool ValidateInputExists(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        PrintError($"Input file not found: {path}");
        return false;
    }
}
