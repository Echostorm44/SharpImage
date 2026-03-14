// SharpImage CLI — Advanced commands: morphology, quantize, dither, composite, compare, fourier, draw.

using System.CommandLine;
using Spectre.Console;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Morphology;
using SharpImage.Transform;

namespace SharpImage.Cli;

public static class AdvancedCommands
{
    public static Command CreateMorphologyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var opOpt = new Option<string>("--op") { Description = "Operation: erode, dilate, open, close, gradient, tophat, bottomhat", DefaultValueFactory = _ => "erode" };
        var kernelOpt = new Option<string>("--kernel") { Description = "Kernel shape: square, diamond, disk, cross", DefaultValueFactory = _ => "square" };
        var sizeOpt = new Option<int>("--size") { Description = "Kernel size (odd number, e.g. 3, 5, 7)", DefaultValueFactory = _ => 3 };
        var iterOpt = new Option<int>("-n", "--iterations") { Description = "Number of iterations", DefaultValueFactory = _ => 1 };
        var cmd = new Command("morphology", """
            Apply morphological operations to an image.
            Useful for noise removal, shape analysis, and binary image processing.

            Operations:
              erode      — Shrink bright regions (remove small bright spots)
              dilate     — Expand bright regions (fill small dark gaps)
              open       — Erode then dilate (remove small objects)
              close      — Dilate then erode (fill small holes)
              gradient   — Difference between dilate and erode (edge outline)
              tophat     — Difference between original and opening (bright details)
              bottomhat  — Difference between closing and original (dark details)

            Kernels: square, diamond, disk, cross

            Examples:
              sharpimage morphology binary.png --op erode --kernel square --size 3 eroded.png
              sharpimage morphology mask.png --op close --kernel disk --size 5 -n 2 closed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(opOpt);
        cmd.Add(kernelOpt);
        cmd.Add(sizeOpt);
        cmd.Add(iterOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string op = parseResult.GetValue(opOpt)!;
            string kernelName = parseResult.GetValue(kernelOpt)!;
            int size = parseResult.GetValue(sizeOpt);
            int iter = parseResult.GetValue(iterOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            var kernel = kernelName.ToLowerInvariant() switch
            {
                "diamond" => MorphologyOps.Kernel.Diamond(size),
                "disk" => MorphologyOps.Kernel.Disk(size),
                "cross" or "plus" => MorphologyOps.Kernel.Plus(size),
                _ => MorphologyOps.Kernel.Square(size)
            };

            CliOutput.RunPipeline(input, output, $"Morphology: {op} ({kernelName} {size}×{size})", img =>
            {
                return op.ToLowerInvariant() switch
                {
                    "dilate" => MorphologyOps.Dilate(img, kernel, iter),
                    "open" => MorphologyOps.Open(img, kernel, iter),
                    "close" => MorphologyOps.Close(img, kernel, iter),
                    "gradient" => MorphologyOps.Gradient(img, kernel),
                    "tophat" => MorphologyOps.TopHat(img, kernel),
                    "bottomhat" => MorphologyOps.BottomHat(img, kernel),
                    _ => MorphologyOps.Erode(img, kernel, iter)
                };
            });
        });
        return cmd;
    }

    public static Command CreateQuantizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var colorsOpt = new Option<int>("-c", "--colors") { Description = "Maximum number of colors (2-256)", DefaultValueFactory = _ => 16 };
        var ditherOpt = new Option<string>("--dither") { Description = "Dither method: none, floyd (Floyd-Steinberg error diffusion)", DefaultValueFactory = _ => "none" };
        var cmd = new Command("quantize", """
            Reduce the number of colors in an image using median-cut quantization.
            Optional Floyd-Steinberg dithering for smoother color transitions.

            Examples:
              sharpimage quantize photo.jpg --colors 16 indexed.png
              sharpimage quantize photo.jpg --colors 256 --dither floyd smooth.png
              sharpimage quantize input.png --colors 4 minimal.gif
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(colorsOpt);
        cmd.Add(ditherOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int colors = parseResult.GetValue(colorsOpt);
            string dither = parseResult.GetValue(ditherOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            var dm = dither.ToLowerInvariant() switch
            {
                "floyd" => SharpImage.Quantize.ColorQuantize.DitherMethod.FloydSteinberg,
                _ => SharpImage.Quantize.ColorQuantize.DitherMethod.None
            };
            CliOutput.RunPipeline(input, output, $"Quantize to {colors} colors",
                img => SharpImage.Quantize.ColorQuantize.Quantize(img, colors, dm));
        });
        return cmd;
    }

    public static Command CreateDitherCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var orderOpt = new Option<int>("--order") { Description = "Bayer matrix order (2, 4, 8, 16)", DefaultValueFactory = _ => 4 };
        var levelsOpt = new Option<int>("-l", "--levels") { Description = "Output levels per channel", DefaultValueFactory = _ => 2 };
        var cmd = new Command("dither", """
            Apply ordered (Bayer) dithering to an image.
            Creates a retro halftone pattern effect.

            Examples:
              sharpimage dither photo.jpg --order 4 --levels 2 dithered.png
              sharpimage dither photo.jpg --order 8 --levels 4 retro.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(orderOpt);
        cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int order = parseResult.GetValue(orderOpt);
            int levels = parseResult.GetValue(levelsOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunMutatingPipeline(input, output, $"Ordered dither (Bayer {order}×{order})", img =>
                SharpImage.Threshold.ThresholdOps.OrderedDither(img, order, levels));
        });
        return cmd;
    }

    public static Command CreateCompositeCommand()
    {
        var baseArg = new Argument<string>("base") { Description = "Base (background) image file path" };
        var overlayArg = new Argument<string>("overlay") { Description = "Overlay (foreground) image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<int>("-x") { Description = "Overlay X offset", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("-y") { Description = "Overlay Y offset", DefaultValueFactory = _ => 0 };
        var modeOpt = new Option<string>("-m", "--mode") { Description = "Blend mode (see below)", DefaultValueFactory = _ => "over" };
        var cmd = new Command("composite", """
            Composite (overlay) one image on top of another.

            Blend modes:
              over           — Standard alpha compositing (Porter-Duff)
              multiply       — Darken by multiplying pixel values
              screen         — Lighten (inverse of multiply)
              overlay        — Combination of multiply and screen
              add            — Add pixel values (brightens)
              subtract       — Subtract overlay from base (darkens)
              replace        — Replace base pixels entirely
              darken         — Per-channel minimum (keeps darkest)
              lighten        — Per-channel maximum (keeps lightest)
              colordodge     — Brighten base by dividing by inverse overlay
              colorburn      — Darken base by dividing inverted base by overlay
              hardlight      — Overlay with base/overlay swapped
              softlight      — Gentle contrast adjustment
              difference     — Absolute difference between channels
              exclusion      — Lower-contrast difference
              dissolve       — Probabilistic compositing by alpha
              atop           — Overlay only where base is opaque (Porter-Duff)
              in             — Overlay clipped to base alpha (Porter-Duff)
              out            — Overlay where base is transparent (Porter-Duff)
              xor            — Non-overlapping regions only (Porter-Duff)
              plus           — Linear addition of premultiplied values
              minus          — Linear subtraction of premultiplied values
              modulusadd     — Modular addition (wrapping)
              modulussubtract— Modular subtraction (wrapping)
              bumpmap        — Modulate base by overlay luminance
              hardmix        — Posterize to black/white based on sum
              linearlight    — Combines Linear Burn and Linear Dodge
              vividlight     — Combines Color Burn and Color Dodge
              pinlight       — Selective replacement based on overlay value

            Examples:
              sharpimage composite background.png input.png -x 10 -y 10 result.png
              sharpimage composite photo.jpg texture.png --mode multiply blended.png
              sharpimage composite base.png overlay.png --mode softlight result.png
            """);
        cmd.Add(baseArg);
        cmd.Add(overlayArg);
        cmd.Add(outputArg);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.Add(modeOpt);
        cmd.SetAction((parseResult) =>
        {
            string basePath = parseResult.GetValue(baseArg)!;
            string overlayPath = parseResult.GetValue(overlayArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            string mode = parseResult.GetValue(modeOpt)!;
            if (!CliOutput.ValidateInputExists(basePath) || !CliOutput.ValidateInputExists(overlayPath)) return;

            var compMode = mode.ToLowerInvariant() switch
            {
                "multiply" => CompositeMode.Multiply,
                "screen" => CompositeMode.Screen,
                "overlay" => CompositeMode.Overlay,
                "add" => CompositeMode.Add,
                "subtract" => CompositeMode.Subtract,
                "replace" => CompositeMode.Replace,
                "darken" => CompositeMode.Darken,
                "lighten" => CompositeMode.Lighten,
                "colordodge" => CompositeMode.ColorDodge,
                "colorburn" => CompositeMode.ColorBurn,
                "hardlight" => CompositeMode.HardLight,
                "softlight" => CompositeMode.SoftLight,
                "difference" => CompositeMode.Difference,
                "exclusion" => CompositeMode.Exclusion,
                "dissolve" => CompositeMode.Dissolve,
                "atop" => CompositeMode.Atop,
                "in" => CompositeMode.In,
                "out" => CompositeMode.Out,
                "xor" => CompositeMode.Xor,
                "plus" => CompositeMode.Plus,
                "minus" => CompositeMode.Minus,
                "modulusadd" => CompositeMode.ModulusAdd,
                "modulussubtract" => CompositeMode.ModulusSubtract,
                "bumpmap" => CompositeMode.Bumpmap,
                "hardmix" => CompositeMode.HardMix,
                "linearlight" => CompositeMode.LinearLight,
                "vividlight" => CompositeMode.VividLight,
                "pinlight" => CompositeMode.PinLight,
                _ => CompositeMode.Over
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();

            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
                .Start(ctx =>
                {
                    var t1 = ctx.AddTask($"[cyan]Reading[/] {Path.GetFileName(basePath)}", maxValue: 100);
                    var baseImg = FormatRegistry.Read(basePath);
                    t1.Value = 100;

                    var t2 = ctx.AddTask($"[cyan]Reading[/] {Path.GetFileName(overlayPath)}", maxValue: 100);
                    var overlay = FormatRegistry.Read(overlayPath);
                    t2.Value = 100;

                    var t3 = ctx.AddTask($"[yellow]Compositing[/] ({mode})", maxValue: 100);
                    SharpImage.Transform.Composite.Apply(baseImg, overlay, x, y, compMode);
                    t3.Value = 100;

                    var t4 = ctx.AddTask($"[green]Writing[/] {Path.GetFileName(output)}", maxValue: 100);
                    FormatRegistry.Write(baseImg, output);
                    t4.Value = 100;
                });

            sw.Stop();
            CliOutput.PrintSuccess(basePath, output, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateCompareCommand()
    {
        var imageA = new Argument<string>("imageA") { Description = "First image file path" };
        var imageB = new Argument<string>("imageB") { Description = "Second image file path" };
        var diffOpt = new Option<string>("--diff") { Description = "Output difference image to this path" };
        var cmd = new Command("compare", """
            Compare two images and report quality metrics.
            Displays MSE, RMSE, PSNR, MAE, SSIM, and pixel error count.

            Examples:
              sharpimage compare original.png compressed.jpg
              sharpimage compare before.png after.png --diff difference.png
            """);
        cmd.Add(imageA);
        cmd.Add(imageB);
        cmd.Add(diffOpt);
        cmd.SetAction((parseResult) =>
        {
            string pathA = parseResult.GetValue(imageA)!;
            string pathB = parseResult.GetValue(imageB)!;
            string? diffPath = parseResult.GetValue(diffOpt);
            if (!CliOutput.ValidateInputExists(pathA) || !CliOutput.ValidateInputExists(pathB)) return;

            var imgA = CliOutput.ReadImageWithProgress(pathA);
            var imgB = CliOutput.ReadImageWithProgress(pathB);

            double mse = SharpImage.Analysis.ImageCompare.MeanSquaredError(imgA, imgB);
            double rmse = SharpImage.Analysis.ImageCompare.RootMeanSquaredError(imgA, imgB);
            double psnr = SharpImage.Analysis.ImageCompare.PeakSignalToNoiseRatio(imgA, imgB);
            double mae = SharpImage.Analysis.ImageCompare.MeanAbsoluteError(imgA, imgB);
            double pae = SharpImage.Analysis.ImageCompare.PeakAbsoluteError(imgA, imgB);
            double ssim = SharpImage.Analysis.ImageCompare.StructuralSimilarity(imgA, imgB);
            long errorCount = SharpImage.Analysis.ImageCompare.AbsoluteErrorCount(imgA, imgB, 0.01);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Metric[/]")
                .AddColumn("[bold]Value[/]")
                .Title("[cyan]Image Comparison Results[/]");

            table.AddRow("MSE", $"{mse:F6}");
            table.AddRow("RMSE", $"{rmse:F6}");
            table.AddRow("PSNR", psnr == double.PositiveInfinity ? "[green]∞ (identical)[/]" : $"{psnr:F2} dB");
            table.AddRow("MAE", $"{mae:F6}");
            table.AddRow("Peak Absolute Error", $"{pae:F6}");
            table.AddRow("SSIM", $"{ssim:F6}");
            table.AddRow("Pixels Differing (>1%)", $"{errorCount:N0}");

            string similarity = ssim switch
            {
                >= 0.99 => "[green]Nearly identical[/]",
                >= 0.95 => "[green]Very similar[/]",
                >= 0.80 => "[yellow]Moderately similar[/]",
                >= 0.50 => "[red]Different[/]",
                _ => "[red]Very different[/]"
            };
            table.AddRow("Assessment", similarity);

            AnsiConsole.Write(table);

            if (diffPath != null)
            {
                var diff = SharpImage.Analysis.ImageCompare.CreateDifferenceImage(imgA, imgB);
                CliOutput.WriteImageWithProgress(diff, diffPath);
                AnsiConsole.MarkupLine($"[dim]Difference image saved to {diffPath}[/]");
            }
        });
        return cmd;
    }

    public static Command CreateFourierCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output magnitude spectrum image" };
        var cmd = new Command("fourier", """
            Compute the 2D FFT magnitude spectrum of an image.
            Produces a log-scaled, frequency-centered visualization.

            Examples:
              sharpimage fourier photo.jpg spectrum.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Computing FFT spectrum",
                SharpImage.Fourier.FourierTransform.MagnitudeSpectrum);
        });
        return cmd;
    }

    public static Command CreateDrawCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var textOpt = new Option<string>("--text") { Description = "Text to draw on the image" };
        var xOpt = new Option<int>("-x") { Description = "X position", DefaultValueFactory = _ => 10 };
        var yOpt = new Option<int>("-y") { Description = "Y position", DefaultValueFactory = _ => 10 };
        var scaleOpt = new Option<int>("--scale") { Description = "Text scale factor", DefaultValueFactory = _ => 2 };
        var colorOpt = new Option<string>("--color") { Description = "Draw color: white, black, red, green, blue, yellow, cyan, magenta", DefaultValueFactory = _ => "white" };
        var cmd = new Command("draw", """
            Draw text or shapes on an image.
            Uses a built-in bitmap font (5×7 pixels, scalable).

            Colors: white, black, red, green, blue, yellow, cyan, magenta

            Examples:
              sharpimage draw photo.jpg --text "Hello World" -x 10 -y 10 annotated.png
              sharpimage draw photo.jpg --text "Copyright 2026" --scale 3 --color red stamped.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(textOpt);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.Add(scaleOpt);
        cmd.Add(colorOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string? text = parseResult.GetValue(textOpt);
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            int scale = parseResult.GetValue(scaleOpt);
            string colorName = parseResult.GetValue(colorOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            if (string.IsNullOrEmpty(text))
            {
                CliOutput.PrintError("--text is required for draw command");
                return;
            }

            var drawColor = colorName.ToLowerInvariant() switch
            {
                "black" => SharpImage.Drawing.DrawColor.Black,
                "red" => SharpImage.Drawing.DrawColor.Red,
                "green" => SharpImage.Drawing.DrawColor.Green,
                "blue" => SharpImage.Drawing.DrawColor.Blue,
                "yellow" => new SharpImage.Drawing.DrawColor(255, 255, 0),
                "cyan" => new SharpImage.Drawing.DrawColor(0, 255, 255),
                "magenta" => new SharpImage.Drawing.DrawColor(255, 0, 255),
                _ => SharpImage.Drawing.DrawColor.White
            };

            CliOutput.RunPipeline(input, output, "Drawing text", img =>
            {
                var ctx = new SharpImage.Drawing.DrawingContext(img);
                ctx.FillColor = drawColor;
                ctx.DrawText(x, y, text, scale);
                return img;
            });
        });
        return cmd;
    }

    // ─── Retouching Commands (Bundle A) ────────────────────────────

    public static Command CreateInpaintCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var maskOpt = new Option<string>("--mask") { Description = "Mask image (non-zero = inpaint area)", Required = true };
        var methodOpt = new Option<string>("--method") { Description = "Inpainting method (telea|ns|patchmatch)", DefaultValueFactory = _ => "telea" };
        var radiusOpt = new Option<int>("--radius") { Description = "Search radius", DefaultValueFactory = _ => 5 };
        var cmd = new Command("inpaint", "Fill masked regions using inpainting algorithms.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(maskOpt); cmd.Add(methodOpt); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string maskPath = parseResult.GetValue(maskOpt)!;
            string method = parseResult.GetValue(methodOpt)!;
            int radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(maskPath)) return;

            var src = FormatRegistry.Read(input);
            var mask = FormatRegistry.Read(maskPath);
            var result = method.ToLowerInvariant() switch
            {
                "ns" => SharpImage.Effects.RetouchingOps.InpaintNavierStokes(src, mask, radius),
                "patchmatch" => SharpImage.Effects.RetouchingOps.PatchMatch(src, mask, radius * 2 - 1),
                _ => SharpImage.Effects.RetouchingOps.InpaintTelea(src, mask, radius),
            };
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); mask.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateCloneStampCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var srcXOpt = new Option<int>("--src-x") { Description = "Source X", Required = true };
        var srcYOpt = new Option<int>("--src-y") { Description = "Source Y", Required = true };
        var dstXOpt = new Option<int>("--dst-x") { Description = "Destination X", Required = true };
        var dstYOpt = new Option<int>("--dst-y") { Description = "Destination Y", Required = true };
        var wOpt = new Option<int>("--width") { Description = "Stamp width", DefaultValueFactory = _ => 20 };
        var hOpt = new Option<int>("--height") { Description = "Stamp height", DefaultValueFactory = _ => 20 };
        var cmd = new Command("clonestamp", "Copy a region from one position to another.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(srcXOpt); cmd.Add(srcYOpt); cmd.Add(dstXOpt); cmd.Add(dstYOpt);
        cmd.Add(wOpt); cmd.Add(hOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int sx = parseResult.GetValue(srcXOpt);
            int sy = parseResult.GetValue(srcYOpt);
            int dx = parseResult.GetValue(dstXOpt);
            int dy = parseResult.GetValue(dstYOpt);
            int w = parseResult.GetValue(wOpt);
            int h = parseResult.GetValue(hOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Clone Stamp",
                img => SharpImage.Effects.RetouchingOps.CloneStamp(img, sx, sy, dx, dy, w, h));
        });
        return cmd;
    }

    public static Command CreateHealingBrushCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var srcXOpt = new Option<int>("--src-x") { Description = "Source X", Required = true };
        var srcYOpt = new Option<int>("--src-y") { Description = "Source Y", Required = true };
        var dstXOpt = new Option<int>("--dst-x") { Description = "Destination X", Required = true };
        var dstYOpt = new Option<int>("--dst-y") { Description = "Destination Y", Required = true };
        var wOpt = new Option<int>("--width") { Description = "Brush width", DefaultValueFactory = _ => 20 };
        var hOpt = new Option<int>("--height") { Description = "Brush height", DefaultValueFactory = _ => 20 };
        var cmd = new Command("healingbrush", "Clone + blend to seamlessly patch a region.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(srcXOpt); cmd.Add(srcYOpt); cmd.Add(dstXOpt); cmd.Add(dstYOpt);
        cmd.Add(wOpt); cmd.Add(hOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int sx = parseResult.GetValue(srcXOpt);
            int sy = parseResult.GetValue(srcYOpt);
            int dx = parseResult.GetValue(dstXOpt);
            int dy = parseResult.GetValue(dstYOpt);
            int w = parseResult.GetValue(wOpt);
            int h = parseResult.GetValue(hOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Healing Brush",
                img => SharpImage.Effects.RetouchingOps.HealingBrush(img, sx, sy, dx, dy, w, h));
        });
        return cmd;
    }

    public static Command CreateRedEyeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cxOpt = new Option<int>("--cx") { Description = "Eye center X", Required = true };
        var cyOpt = new Option<int>("--cy") { Description = "Eye center Y", Required = true };
        var radiusOpt = new Option<int>("--radius") { Description = "Eye radius", DefaultValueFactory = _ => 10 };
        var threshOpt = new Option<double>("--threshold") { Description = "Red detection threshold (0-1)", DefaultValueFactory = _ => 0.6 };
        var cmd = new Command("redeye", "Remove red-eye in a circular region.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(cxOpt); cmd.Add(cyOpt); cmd.Add(radiusOpt); cmd.Add(threshOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int cx = parseResult.GetValue(cxOpt);
            int cy = parseResult.GetValue(cyOpt);
            int r = parseResult.GetValue(radiusOpt);
            double t = parseResult.GetValue(threshOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Red-Eye Removal",
                img => SharpImage.Effects.RetouchingOps.RedEyeRemoval(img, cx, cy, r, t));
        });
        return cmd;
    }

    // ─── Bundle B: Selection & Masking CLI commands ─────────────────

    public static Command CreateFloodSelectCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output mask file path" };
        var seedXOpt = new Option<int>("--seed-x") { Description = "Seed point X coordinate", Required = true };
        var seedYOpt = new Option<int>("--seed-y") { Description = "Seed point Y coordinate", Required = true };
        var toleranceOpt = new Option<double>("--tolerance") { Description = "Color tolerance (0-100%)", DefaultValueFactory = _ => 10.0 };
        var eightOpt = new Option<bool>("--eight-connected") { Description = "Use 8-connectivity instead of 4", DefaultValueFactory = _ => false };
        var cmd = new Command("floodselect", "Select a contiguous region of similar color (magic wand).");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(seedXOpt); cmd.Add(seedYOpt); cmd.Add(toleranceOpt); cmd.Add(eightOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int sx = parseResult.GetValue(seedXOpt);
            int sy = parseResult.GetValue(seedYOpt);
            double tol = parseResult.GetValue(toleranceOpt);
            bool eight = parseResult.GetValue(eightOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Flood Select",
                img => SharpImage.Effects.SelectionOps.FloodSelect(img, sx, sy, tol, eight));
        });
        return cmd;
    }

    public static Command CreateColorSelectCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output mask file path" };
        var colorOpt = new Option<string>("--color") { Description = "Target color (hex, e.g. FF0000)", Required = true };
        var toleranceOpt = new Option<double>("--tolerance") { Description = "Color tolerance (0-100%)", DefaultValueFactory = _ => 10.0 };
        var cmd = new Command("colorselect", "Select all pixels matching a color anywhere in the image.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(colorOpt); cmd.Add(toleranceOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string colorHex = parseResult.GetValue(colorOpt)!;
            double tol = parseResult.GetValue(toleranceOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            var (cr, cg, cb) = EffectsCommands.ParseColor(colorHex);
            CliOutput.RunPipeline(input, output, "Color Range Select",
                img => SharpImage.Effects.SelectionOps.ColorRangeSelect(img, cr, cg, cb, tol));
        });
        return cmd;
    }

    public static Command CreateGrabCutCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var trimapArg = new Argument<string>("trimap") { Description = "Trimap file (black=BG, white=FG, gray=unknown)" };
        var outputArg = new Argument<string>("output") { Description = "Output mask file path" };
        var iterOpt = new Option<int>("--iterations") { Description = "Number of iterations", DefaultValueFactory = _ => 5 };
        var cmd = new Command("grabcut", "Foreground extraction using GrabCut segmentation.");
        cmd.Add(inputArg); cmd.Add(trimapArg); cmd.Add(outputArg); cmd.Add(iterOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string trimapPath = parseResult.GetValue(trimapArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int iter = parseResult.GetValue(iterOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(trimapPath)) return;

            var src = FormatRegistry.Read(input);
            var trimap = FormatRegistry.Read(trimapPath);
            var result = SharpImage.Effects.SelectionOps.GrabCut(src, trimap, iter);
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); trimap.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateFeatherCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input mask file path" };
        var outputArg = new Argument<string>("output") { Description = "Output feathered mask file path" };
        var radiusOpt = new Option<double>("--radius") { Description = "Feather radius in pixels", DefaultValueFactory = _ => 5.0 };
        var cmd = new Command("feather", "Feather (soften) the edges of a selection mask.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Feather Mask",
                img => SharpImage.Effects.SelectionOps.FeatherMask(img, radius));
        });
        return cmd;
    }

    public static Command CreateMaskOpCommand()
    {
        var inputAArg = new Argument<string>("inputA") { Description = "First mask file path" };
        var inputBArg = new Argument<string>("inputB") { Description = "Second mask file path" };
        var outputArg = new Argument<string>("output") { Description = "Output mask file path" };
        var opOpt = new Option<string>("--op") { Description = "Operation: union, intersect, subtract, xor, invert, expand, contract", Required = true };
        var radiusOpt = new Option<int>("--radius") { Description = "Radius for expand/contract operations (default 3)", DefaultValueFactory = _ => 3 };
        var cmd = new Command("maskop", "Perform boolean and morphological operations on selection masks.");
        cmd.Add(inputAArg); cmd.Add(inputBArg); cmd.Add(outputArg); cmd.Add(opOpt); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string inputA = parseResult.GetValue(inputAArg)!;
            string inputB = parseResult.GetValue(inputBArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string op = parseResult.GetValue(opOpt)!.ToLowerInvariant();
            if (!CliOutput.ValidateInputExists(inputA)) return;

            var maskA = FormatRegistry.Read(inputA);

            ImageFrame result;
            if (op == "invert")
            {
                result = SharpImage.Effects.SelectionOps.InvertMask(maskA);
            }
            else if (op == "expand")
            {
                int radius = parseResult.GetValue(radiusOpt);
                result = SharpImage.Effects.SelectionOps.ExpandMask(maskA, radius);
            }
            else if (op == "contract")
            {
                int radius = parseResult.GetValue(radiusOpt);
                result = SharpImage.Effects.SelectionOps.ContractMask(maskA, radius);
            }
            else
            {
                if (!CliOutput.ValidateInputExists(inputB)) return;
                var maskB = FormatRegistry.Read(inputB);
                result = op switch
                {
                    "union" => SharpImage.Effects.SelectionOps.UnionMasks(maskA, maskB),
                    "intersect" => SharpImage.Effects.SelectionOps.IntersectMasks(maskA, maskB),
                    "subtract" => SharpImage.Effects.SelectionOps.SubtractMasks(maskA, maskB),
                    "xor" => SharpImage.Effects.SelectionOps.XorMasks(maskA, maskB),
                    _ => throw new ArgumentException($"Unknown mask operation: {op}")
                };
                maskB.Dispose();
            }

            CliOutput.WriteImageWithProgress(result, output);
            maskA.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateApplyMaskCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var maskArg = new Argument<string>("mask") { Description = "Mask file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path (with alpha)" };
        var cmd = new Command("applymask", "Apply a mask to an image as its alpha channel.");
        cmd.Add(inputArg); cmd.Add(maskArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string maskPath = parseResult.GetValue(maskArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(maskPath)) return;

            var src = FormatRegistry.Read(input);
            var mask = FormatRegistry.Read(maskPath);
            var result = SharpImage.Effects.SelectionOps.ApplyMask(src, mask);
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); mask.Dispose(); result.Dispose();
        });
        return cmd;
    }

    // ─── Bundle E: Stitching CLI commands ───────────────────────────

    public static Command CreateStitchCommand()
    {
        var inputAArg = new Argument<string>("inputA") { Description = "First image file path" };
        var inputBArg = new Argument<string>("inputB") { Description = "Second image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output panorama file path" };
        var cornersOpt = new Option<int>("--max-corners") { Description = "Maximum corners to detect", DefaultValueFactory = _ => 500 };
        var levelsOpt = new Option<int>("--blend-levels") { Description = "Pyramid blend levels", DefaultValueFactory = _ => 4 };
        var cmd = new Command("stitch", "Stitch two overlapping images into a panorama.");
        cmd.Add(inputAArg); cmd.Add(inputBArg); cmd.Add(outputArg);
        cmd.Add(cornersOpt); cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string inputA = parseResult.GetValue(inputAArg)!;
            string inputB = parseResult.GetValue(inputBArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int corners = parseResult.GetValue(cornersOpt);
            int levels = parseResult.GetValue(levelsOpt);
            if (!CliOutput.ValidateInputExists(inputA)) return;
            if (!CliOutput.ValidateInputExists(inputB)) return;

            var imgA = FormatRegistry.Read(inputA);
            var imgB = FormatRegistry.Read(inputB);
            var result = SharpImage.Effects.StitchingOps.StitchPanorama(imgA, imgB, corners, pyramidLevels: levels);
            CliOutput.WriteImageWithProgress(result, output);
            imgA.Dispose(); imgB.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateLaplacianBlendCommand()
    {
        var inputAArg = new Argument<string>("inputA") { Description = "First image file path" };
        var inputBArg = new Argument<string>("inputB") { Description = "Second image file path" };
        var maskArg = new Argument<string>("mask") { Description = "Blend mask file path (white=A, black=B)" };
        var outputArg = new Argument<string>("output") { Description = "Output blended image file path" };
        var levelsOpt = new Option<int>("--levels") { Description = "Pyramid levels", DefaultValueFactory = _ => 4 };
        var cmd = new Command("lapblend", "Multi-band Laplacian pyramid blending of two images.");
        cmd.Add(inputAArg); cmd.Add(inputBArg); cmd.Add(maskArg); cmd.Add(outputArg); cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string inputA = parseResult.GetValue(inputAArg)!;
            string inputB = parseResult.GetValue(inputBArg)!;
            string maskPath = parseResult.GetValue(maskArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int levels = parseResult.GetValue(levelsOpt);
            if (!CliOutput.ValidateInputExists(inputA)) return;
            if (!CliOutput.ValidateInputExists(inputB)) return;
            if (!CliOutput.ValidateInputExists(maskPath)) return;

            var imgA = FormatRegistry.Read(inputA);
            var imgB = FormatRegistry.Read(inputB);
            var mask = FormatRegistry.Read(maskPath);
            var result = SharpImage.Effects.StitchingOps.LaplacianBlend(imgA, imgB, mask, levels);
            CliOutput.WriteImageWithProgress(result, output);
            imgA.Dispose(); imgB.Dispose(); mask.Dispose(); result.Dispose();
        });
        return cmd;
    }

    // ─── Bundle H: Smart Removal CLI commands ───────────────────────

    public static Command CreateSaliencyMapCommand()
    {
        var cmd = new Command("saliency", "Compute saliency map highlighting visually important regions.");
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output saliency map file path" };
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!,
                "Saliency map", img => SharpImage.Effects.SmartRemovalOps.SaliencyMap(img));
        });
        return cmd;
    }

    public static Command CreateAutoBackgroundRemoveCommand()
    {
        var cmd = new Command("bgremove", "Automatically remove the background from an image.");
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output RGBA image file path" };
        var threshOpt = new Option<double>("--threshold") { Description = "Saliency threshold (0.0-1.0)", DefaultValueFactory = _ => 0.4 };
        var iterOpt = new Option<int>("--iterations") { Description = "GrabCut iterations", DefaultValueFactory = _ => 5 };
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(threshOpt); cmd.Add(iterOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double thresh = parseResult.GetValue(threshOpt);
            int iters = parseResult.GetValue(iterOpt);
            CliOutput.RunPipeline(input, output, "Auto background remove",
                img => SharpImage.Effects.SmartRemovalOps.AutoBackgroundRemove(img, thresh, grabCutIterations: iters));
        });
        return cmd;
    }

    public static Command CreateContentAwareFillCommand()
    {
        var cmd = new Command("cafill", "Content-aware fill of masked regions.");
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var maskArg = new Argument<string>("mask") { Description = "Mask file path (white = fill region)" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var patchOpt = new Option<int>("--patch-size") { Description = "Patch size for fill", DefaultValueFactory = _ => 9 };
        cmd.Add(inputArg); cmd.Add(maskArg); cmd.Add(outputArg); cmd.Add(patchOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string maskPath = parseResult.GetValue(maskArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int patch = parseResult.GetValue(patchOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(maskPath)) return;

            var src = FormatRegistry.Read(input);
            var mask = FormatRegistry.Read(maskPath);
            var result = SharpImage.Effects.SmartRemovalOps.ContentAwareFill(src, mask, patch);
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); mask.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateObjectRemoveCommand()
    {
        var cmd = new Command("objremove", "Remove an object defined by a mask and fill seamlessly.");
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var maskArg = new Argument<string>("mask") { Description = "Mask file path (white = object region)" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var patchOpt = new Option<int>("--patch-size") { Description = "Patch size for fill", DefaultValueFactory = _ => 9 };
        var featherOpt = new Option<double>("--feather") { Description = "Feather radius for blending", DefaultValueFactory = _ => 3.0 };
        cmd.Add(inputArg); cmd.Add(maskArg); cmd.Add(outputArg); cmd.Add(patchOpt); cmd.Add(featherOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string maskPath = parseResult.GetValue(maskArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int patch = parseResult.GetValue(patchOpt);
            double feather = parseResult.GetValue(featherOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(maskPath)) return;

            var src = FormatRegistry.Read(input);
            var mask = FormatRegistry.Read(maskPath);
            var result = SharpImage.Effects.SmartRemovalOps.ObjectRemove(src, mask, patch, feather);
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); mask.Dispose(); result.Dispose();
        });
        return cmd;
    }

    // ─── Bundle M: Displacement & Maps ──────────────────────────

    public static Command CreateDisplacementMapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var mapArg = new Argument<string>("map") { Description = "Displacement map image (R=X, G=Y)" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var scaleXOpt = new Option<double>("--sx") { Description = "Horizontal scale", DefaultValueFactory = _ => 20.0 };
        var scaleYOpt = new Option<double>("--sy") { Description = "Vertical scale", DefaultValueFactory = _ => 20.0 };
        var cmd = new Command("displace", "Distort image using a displacement map.");
        cmd.Add(inputArg); cmd.Add(mapArg); cmd.Add(outputArg); cmd.Add(scaleXOpt); cmd.Add(scaleYOpt);
        cmd.SetAction((parseResult) =>
        {
            var src = FormatRegistry.Read(parseResult.GetValue(inputArg)!);
            var map = FormatRegistry.Read(parseResult.GetValue(mapArg)!);
            var result = SharpImage.Effects.DisplacementOps.DisplacementMap(src, map,
                parseResult.GetValue(scaleXOpt), parseResult.GetValue(scaleYOpt));
            CliOutput.WriteImageWithProgress(result, parseResult.GetValue(outputArg)!);
            src.Dispose(); map.Dispose(); result.Dispose();
        });
        return cmd;
    }

    public static Command CreateNormalMapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Grayscale height map" };
        var outputArg = new Argument<string>("output") { Description = "Output normal map" };
        var strengthOpt = new Option<double>("--strength") { Description = "Normal strength", DefaultValueFactory = _ => 1.0 };
        var cmd = new Command("normalmap", "Generate tangent-space normal map from height map.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(strengthOpt);
        cmd.SetAction((parseResult) =>
        {
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Normal Map", img =>
                SharpImage.Effects.DisplacementOps.NormalMapFromHeight(img, parseResult.GetValue(strengthOpt)));
        });
        return cmd;
    }

    public static Command CreateSpherizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var amountOpt = new Option<double>("--amount") { Description = "Spherize amount (0-1)", DefaultValueFactory = _ => 0.7 };
        var cmd = new Command("spherize", "Wrap image onto a sphere surface.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(amountOpt);
        cmd.SetAction((parseResult) =>
        {
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Spherize", img =>
                SharpImage.Effects.DisplacementOps.Spherize(img, parseResult.GetValue(amountOpt)));
        });
        return cmd;
    }

    // ─── Bundle O: Texture Tools ────────────────────────────────

    public static Command CreateSeamlessTileCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var blendOpt = new Option<double>("--blend") { Description = "Blend width fraction (0-0.5)", DefaultValueFactory = _ => 0.25 };
        var cmd = new Command("seamlesstile", "Make an image tileable by blending edges.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(blendOpt);
        cmd.SetAction((parseResult) =>
        {
            CliOutput.RunPipeline(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputArg)!, "Seamless Tile", img =>
                SharpImage.Effects.TextureOps.MakeSeamlessTile(img, parseResult.GetValue(blendOpt)));
        });
        return cmd;
    }

    public static Command CreateTexSynthCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Exemplar texture patch" };
        var outputArg = new Argument<string>("output") { Description = "Output synthesized texture" };
        var widthOpt = new Option<uint>("--width") { Description = "Output width", DefaultValueFactory = _ => 256u };
        var heightOpt = new Option<uint>("--height") { Description = "Output height", DefaultValueFactory = _ => 256u };
        var radiusOpt = new Option<int>("--radius") { Description = "Neighborhood radius", DefaultValueFactory = _ => 3 };
        var seedOpt = new Option<int>("--seed") { Description = "Random seed", DefaultValueFactory = _ => 42 };
        var cmd = new Command("texsynth", "Synthesize larger texture from exemplar patch.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(widthOpt); cmd.Add(heightOpt); cmd.Add(radiusOpt); cmd.Add(seedOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var src = FormatRegistry.Read(input);
            var result = SharpImage.Effects.TextureOps.SynthesizeTexture(src,
                parseResult.GetValue(widthOpt), parseResult.GetValue(heightOpt),
                parseResult.GetValue(radiusOpt), parseResult.GetValue(seedOpt));
            CliOutput.WriteImageWithProgress(result, output);
            src.Dispose(); result.Dispose();
        });
        return cmd;
    }

    // ─── Bundle P: Game Dev Utilities ───────────────────────────

    public static Command CreateSpriteSheetCommand()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "Input sprite image files", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string>("-o", "--output") { Description = "Output atlas image path" };
        outputOpt.Required = true;
        var jsonOpt = new Option<string?>("--json") { Description = "Output JSON metadata path" };
        var paddingOpt = new Option<int>("--padding") { Description = "Padding between sprites", DefaultValueFactory = _ => 1 };
        var colsOpt = new Option<int>("--cols") { Description = "Number of columns (0=auto)", DefaultValueFactory = _ => 0 };
        var cmd = new Command("spritesheet", "Pack multiple images into a sprite sheet atlas with JSON metadata.");
        cmd.Add(inputsArg); cmd.Add(outputOpt); cmd.Add(jsonOpt); cmd.Add(paddingOpt); cmd.Add(colsOpt);
        cmd.SetAction((parseResult) =>
        {
            var inputs = parseResult.GetValue(inputsArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var jsonPath = parseResult.GetValue(jsonOpt);
            var sprites = inputs.Select(p => FormatRegistry.Read(p)).ToArray();
            var names = inputs.Select(Path.GetFileNameWithoutExtension).ToArray();
            var (atlas, json) = SharpImage.Effects.GameDevOps.PackSpriteSheet(sprites, names!,
                parseResult.GetValue(paddingOpt), parseResult.GetValue(colsOpt));
            CliOutput.WriteImageWithProgress(atlas, output);
            if (jsonPath != null) File.WriteAllText(jsonPath, json);
            else Console.WriteLine(json);
            foreach (var s in sprites) s.Dispose();
            atlas.Dispose();
        });
        return cmd;
    }

    public static Command CreateCubemapExtractCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Equirectangular panorama image" };
        var outputArg = new Argument<string>("output") { Description = "Output path prefix (e.g. sky_ → sky_px.png)" };
        var sizeOpt = new Option<uint>("--size") { Description = "Face size", DefaultValueFactory = _ => 256u };
        var cmd = new Command("cubemapextract", "Extract 6 cubemap faces from an equirectangular panorama.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(sizeOpt);
        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var prefix = parseResult.GetValue(outputArg)!;
            var size = parseResult.GetValue(sizeOpt);
            var src = FormatRegistry.Read(input);
            var ext = Path.GetExtension(prefix);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            var baseName = Path.ChangeExtension(prefix, null);

            foreach (var face in Enum.GetValues<SharpImage.Effects.CubeFace>())
            {
                var faceName = face.ToString().ToLowerInvariant();
                var faceImg = SharpImage.Effects.GameDevOps.EquirectToCubeFace(src, face, size);
                var path = $"{baseName}_{faceName}{ext}";
                CliOutput.WriteImageWithProgress(faceImg, path);
                faceImg.Dispose();
            }
            src.Dispose();
        });
        return cmd;
    }

    public static Command CreateCubemapStitchCommand()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "6 face images: +X -X +Y -Y +Z -Z", Arity = new ArgumentArity(6, 6) };
        var outputArg = new Argument<string>("output") { Description = "Output equirectangular image" };
        var widthOpt = new Option<uint>("--width") { Description = "Output width", DefaultValueFactory = _ => 512u };
        var cmd = new Command("cubemapstitch", "Stitch 6 cubemap faces into an equirectangular panorama.");
        cmd.Add(inputsArg); cmd.Add(outputArg); cmd.Add(widthOpt);
        cmd.SetAction((parseResult) =>
        {
            var inputs = parseResult.GetValue(inputsArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var width = parseResult.GetValue(widthOpt);
            var faces = new Dictionary<SharpImage.Effects.CubeFace, SharpImage.Image.ImageFrame>();
            var faceOrder = new[] { SharpImage.Effects.CubeFace.PositiveX, SharpImage.Effects.CubeFace.NegativeX,
                SharpImage.Effects.CubeFace.PositiveY, SharpImage.Effects.CubeFace.NegativeY,
                SharpImage.Effects.CubeFace.PositiveZ, SharpImage.Effects.CubeFace.NegativeZ };
            for (int i = 0; i < 6; i++)
                faces[faceOrder[i]] = FormatRegistry.Read(inputs[i]);
            var result = SharpImage.Effects.GameDevOps.CubemapToEquirect(faces, width, width / 2);
            CliOutput.WriteImageWithProgress(result, output);
            foreach (var f in faces.Values) f.Dispose();
            result.Dispose();
        });
        return cmd;
    }

    public static Command CreateAlphaMattingCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Source image file path" };
        var trimapArg = new Argument<string>("trimap") { Description = "Trimap image (white=foreground, black=background, gray=unknown)" };
        var outputArg = new Argument<string>("output") { Description = "Output alpha matte file path" };
        var kOpt = new Option<int>("--k") { Description = "Number of nearest neighbors (default 20)", DefaultValueFactory = _ => 20 };
        var sigmaOpt = new Option<double>("--sigma") { Description = "Color similarity sigma (default 10.0)", DefaultValueFactory = _ => 10.0 };
        var cmd = new Command("alphamatting", """
            Compute a soft alpha matte from a source image and trimap.
            The trimap classifies pixels as foreground (white), background (black),
            or unknown (gray). The algorithm estimates alpha for unknown regions.

            Examples:
              sharpimage alphamatting photo.jpg trimap.png matte.png
              sharpimage alphamatting photo.jpg trimap.png matte.png --k 30 --sigma 15
            """);
        cmd.Add(inputArg); cmd.Add(trimapArg); cmd.Add(outputArg);
        cmd.Add(kOpt); cmd.Add(sigmaOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string trimap = parseResult.GetValue(trimapArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(trimap)) return;

            int k = parseResult.GetValue(kOpt);
            double sigma = parseResult.GetValue(sigmaOpt);

            var src = FormatRegistry.Read(input);
            var trimapImg = FormatRegistry.Read(trimap);

            try
            {
                var result = SharpImage.Effects.SelectionOps.AlphaMatting(src, trimapImg, k, sigma);
                CliOutput.WriteImageWithProgress(result, output);
                result.Dispose();
            }
            catch (Exception ex)
            {
                CliOutput.PrintError(ex.Message);
            }
            finally
            {
                src.Dispose();
                trimapImg.Dispose();
            }
        });
        return cmd;
    }
}
