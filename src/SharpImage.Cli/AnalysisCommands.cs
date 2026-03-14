// SharpImage CLI — Analysis & detection commands: phash, canny, houghlines, connectedcomponents, meanshift.

using System.CommandLine;
using Spectre.Console;
using SharpImage.Analysis;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Cli;

public static class AnalysisCommands
{
    public static Command CreatePhashCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var input2Opt = new Option<string?>("--compare") { Description = "Optional second image to compute Hamming distance" };
        var cmd = new Command("phash", """
            Compute a perceptual hash (pHash) for an image.
            Returns a 64-bit DCT-based fingerprint stable under minor edits.
            Optionally compare two images by Hamming distance.

            Examples:
              sharpimage phash photo.jpg
              sharpimage phash photo.jpg --compare edited.jpg
            """);
        cmd.Add(inputArg);
        cmd.Add(input2Opt);
        cmd.SetAction((parseResult) =>
        {
            string path = parseResult.GetValue(inputArg)!;
            string? path2 = parseResult.GetValue(input2Opt);
            if (!CliOutput.ValidateInputExists(path)) return;

            using var img = FormatRegistry.Read(path);
            ulong hash1 = PerceptualHash.Compute(img);

            AnsiConsole.MarkupLine($"[bold cyan]Perceptual Hash:[/] {PerceptualHash.ToHexString(hash1)}");

            if (path2 != null)
            {
                if (!CliOutput.ValidateInputExists(path2)) return;
                using var img2 = FormatRegistry.Read(path2);
                ulong hash2 = PerceptualHash.Compute(img2);
                int distance = PerceptualHash.HammingDistance(hash1, hash2);
                bool similar = PerceptualHash.AreSimilar(hash1, hash2);

                AnsiConsole.MarkupLine($"[bold cyan]Compare Hash:[/]   {PerceptualHash.ToHexString(hash2)}");
                AnsiConsole.MarkupLine($"[bold cyan]Hamming Dist:[/]   {distance}/64");
                AnsiConsole.MarkupLine(similar
                    ? "[bold green]Result: SIMILAR[/]"
                    : "[bold red]Result: DIFFERENT[/]");
            }
        });
        return cmd;
    }

    public static Command CreateCannyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sigmaOpt = new Option<double>("--sigma") { Description = "Gaussian blur sigma (default: 1.4)", DefaultValueFactory = _ => 1.4 };
        var lowOpt = new Option<double>("--low") { Description = "Low hysteresis threshold 0..1 (default: 0.1)", DefaultValueFactory = _ => 0.1 };
        var highOpt = new Option<double>("--high") { Description = "High hysteresis threshold 0..1 (default: 0.3)", DefaultValueFactory = _ => 0.3 };
        var cmd = new Command("canny", """
            Apply Canny edge detection to an image.
            Produces clean, thin, well-connected edges using:
              1. Gaussian smoothing (noise reduction)
              2. Sobel gradient computation
              3. Non-maximum suppression (edge thinning)
              4. Double-threshold hysteresis (edge linking)

            Output is a binary image (white edges on black background).

            Examples:
              sharpimage canny photo.jpg edges.png
              sharpimage canny photo.jpg edges.png --sigma 2.0 --low 0.05 --high 0.2
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(sigmaOpt);
        cmd.Add(lowOpt);
        cmd.Add(highOpt);
        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            double sigma = parseResult.GetValue(sigmaOpt);
            double low = parseResult.GetValue(lowOpt);
            double high = parseResult.GetValue(highOpt);
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

                    var t2 = ctx.AddTask("[cyan]Canny edge detection[/]", maxValue: 100);
                    using var result = CannyEdge.Detect(img, sigma, low, high);
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    FormatRegistry.Write(result, outPath);
                    t3.Value = 100;
                });
            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateHoughCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input edge image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image with lines drawn" };
        var threshOpt = new Option<int>("--threshold") { Description = "Min vote count for line detection (default: 100)", DefaultValueFactory = _ => 100 };
        var maxOpt = new Option<int>("--max") { Description = "Maximum lines to draw (default: 20)", DefaultValueFactory = _ => 20 };
        var cmd = new Command("houghlines", """
            Detect straight lines in an edge image using the Hough transform.
            Best used on binary edge images (e.g., output from 'canny' command).
            Draws detected lines on the output image.

            Examples:
              sharpimage canny photo.jpg edges.png
              sharpimage houghlines edges.png lines.png --threshold 50 --max 10
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(threshOpt);
        cmd.Add(maxOpt);
        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            int threshold = parseResult.GetValue(threshOpt);
            int maxLines = parseResult.GetValue(maxOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int lineCount = 0;
            AnsiConsole.Progress()
                .AutoClear(false).HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask("[cyan]Reading[/]", maxValue: 100);
                    using var img = FormatRegistry.Read(inPath);
                    t1.Value = 100;

                    var t2 = ctx.AddTask("[cyan]Hough transform[/]", maxValue: 100);
                    var lines = HoughLines.Detect(img, threshold);
                    lineCount = lines.Length;
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Drawing lines[/]", maxValue: 100);
                    HoughLines.DrawLines(img, lines, maxLines);
                    t3.Value = 100;

                    var t4 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    FormatRegistry.Write(img, outPath);
                    t4.Value = 100;
                });

            AnsiConsole.MarkupLine($"[bold cyan]Lines detected:[/] {lineCount}");
            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateConnectedComponentsCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output color-coded label image" };
        var threshOpt = new Option<double>("--threshold") { Description = "Intensity threshold 0..1 (default: 0.5)", DefaultValueFactory = _ => 0.5 };
        var connOpt = new Option<int>("--connectivity") { Description = "4 or 8 connectivity (default: 8)", DefaultValueFactory = _ => 8 };
        var cmd = new Command("connectedcomponents", """
            Connected-component labeling (object detection).
            Identifies and counts distinct objects in a binary/thresholded image.
            Reports area, bounding box, and centroid for each component.
            Output is a color-coded image where each object has a unique color.

            Examples:
              sharpimage connectedcomponents binary.png labeled.png
              sharpimage connectedcomponents binary.png labeled.png --threshold 0.3 --connectivity 4
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(threshOpt);
        cmd.Add(connOpt);
        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            double threshold = parseResult.GetValue(threshOpt);
            int connectivity = parseResult.GetValue(connOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            ConnectedComponents.CclResult result = default;
            AnsiConsole.Progress()
                .AutoClear(false).HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask("[cyan]Reading[/]", maxValue: 100);
                    using var img = FormatRegistry.Read(inPath);
                    t1.Value = 100;

                    var t2 = ctx.AddTask("[cyan]Analyzing components[/]", maxValue: 100);
                    result = ConnectedComponents.Analyze(img, threshold, connectivity);
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Rendering labels[/]", maxValue: 100);
                    using var rendered = ConnectedComponents.RenderLabels(result);
                    t3.Value = 100;

                    var t4 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    FormatRegistry.Write(rendered, outPath);
                    t4.Value = 100;
                });

            AnsiConsole.MarkupLine($"[bold cyan]Objects found:[/] {result.ObjectCount}");
            if (result.Components != null)
            {
                var table = new Table().AddColumn("Label").AddColumn("Area").AddColumn("BBox").AddColumn("Centroid");
                for (int i = 1; i < result.Components.Length && i <= 20; i++)
                {
                    var c = result.Components[i];
                    table.AddRow(
                        c.Label.ToString(),
                        c.Area.ToString(),
                        $"({c.BoundingBox.X},{c.BoundingBox.Y}) {c.BoundingBox.Width}×{c.BoundingBox.Height}",
                        $"({c.Centroid.X:F1},{c.Centroid.Y:F1})"
                    );
                }
                AnsiConsole.Write(table);
            }

            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateMeanShiftCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output segmented image" };
        var spatialOpt = new Option<int>("--spatial") { Description = "Spatial radius in pixels (default: 10)", DefaultValueFactory = _ => 10 };
        var colorOpt = new Option<double>("--color") { Description = "Color radius 0..1 (default: 0.15)", DefaultValueFactory = _ => 0.15 };
        var iterOpt = new Option<int>("--iterations") { Description = "Max iterations per pixel (default: 10)", DefaultValueFactory = _ => 10 };
        var cmd = new Command("meanshift", """
            Apply mean-shift color segmentation.
            Groups similar nearby colors into flat regions, producing a posterized/segmented look.
            Useful for object extraction and simplification.

            Parameters:
              --spatial   Larger values merge more distant pixels
              --color     Larger values merge more different colors
              --iterations  More iterations = smoother convergence

            Examples:
              sharpimage meanshift photo.jpg segmented.png
              sharpimage meanshift photo.jpg segmented.png --spatial 15 --color 0.2
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(spatialOpt);
        cmd.Add(colorOpt);
        cmd.Add(iterOpt);
        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            int spatial = parseResult.GetValue(spatialOpt);
            double color = parseResult.GetValue(colorOpt);
            int maxIter = parseResult.GetValue(iterOpt);
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

                    var t2 = ctx.AddTask("[cyan]Mean-shift segmentation[/]", maxValue: 100);
                    using var result = MeanShift.Segment(img, spatial, color, maxIter);
                    t2.Value = 100;

                    var t3 = ctx.AddTask("[cyan]Writing[/]", maxValue: 100);
                    FormatRegistry.Write(result, outPath);
                    t3.Value = 100;
                });
            CliOutput.PrintSuccess(inPath, outPath, sw.Elapsed);
        });
        return cmd;
    }
}
