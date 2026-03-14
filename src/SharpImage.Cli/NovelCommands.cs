// SharpImage CLI — Novel feature commands.
// Content-aware resize (seam carving), energy map, hash suite, histogram, smart crop, pipeline, tensor export, palette, perceptual diff, animated webp, modern dithering.

using SharpImage.Analysis;
using SharpImage.Formats;
using SharpImage.Threshold;
using SharpImage.Transform;
using Spectre.Console;
using System.CommandLine;

namespace SharpImage.Cli;

public static class NovelCommands
{
    public static Command CreateSeamCarveCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "Target width in pixels", Required = true };
        var heightOpt = new Option<int>("-h", "--height") { Description = "Target height in pixels", Required = true };
        var cmd = new Command("seamcarve", """
            Content-aware resize using seam carving.
            Intelligently removes low-energy seams to reduce image dimensions
            while preserving visually important content. Unlike standard resize,
            seam carving avoids stretching or squashing important features.

            Target dimensions must be smaller than or equal to the source dimensions.

            Examples:
              sharpimage seamcarve photo.jpg resized.png -w 800 -h 600
              sharpimage seamcarve landscape.png narrow.png -w 400 -h 600
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int width = parseResult.GetValue(widthOpt);
            int height = parseResult.GetValue(heightOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, $"Seam carving ({width}×{height})",
                img => SeamCarving.Apply(img, width, height));
        });
        return cmd;
    }

    public static Command CreateEnergyMapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output energy map image path" };
        var cmd = new Command("energymap", """
            Visualize the energy map used by seam carving.
            Shows pixel importance: bright = high energy (important), dark = low energy (removable).
            Useful for understanding what content-aware resize will preserve vs remove.

            Examples:
              sharpimage energymap photo.jpg energy.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Computing energy map",
                img => SeamCarving.GetEnergyMap(img));
        });
        return cmd;
    }

    public static Command CreateHashSuiteCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var input2Opt = new Option<string?>("--compare") { Description = "Optional second image to compare all hashes" };
        var cmd = new Command("hashsuite", """
            Compute all four perceptual hash types for an image:
              aHash (average) — fastest, good for near-exact duplicates
              dHash (difference) — gradient-based, robust to brightness changes
              pHash (perceptual) — DCT-based, gold standard for similarity
              wHash (wavelet) — Haar wavelet, resilient to geometric transforms

            Each hash is a 64-bit fingerprint. Lower Hamming distance = more similar.

            Examples:
              sharpimage hashsuite photo.jpg
              sharpimage hashsuite photo.jpg --compare edited.jpg
            """);
        cmd.Add(inputArg);
        cmd.Add(input2Opt);
        cmd.SetAction((parseResult) =>
        {
            string path = parseResult.GetValue(inputArg)!;
            string? path2 = parseResult.GetValue(input2Opt);
            if (!CliOutput.ValidateInputExists(path)) return;

            using var img = FormatRegistry.Read(path);
            var hashes = PerceptualHash.ComputeAll(img);

            var table = new Table();
            table.AddColumn("[bold]Hash Type[/]");
            table.AddColumn("[bold]Value[/]");
            table.AddRow("aHash (average)", PerceptualHash.ToHexString(hashes.AverageHash));
            table.AddRow("dHash (difference)", PerceptualHash.ToHexString(hashes.DifferenceHash));
            table.AddRow("pHash (perceptual)", PerceptualHash.ToHexString(hashes.PerceptualHash));
            table.AddRow("wHash (wavelet)", PerceptualHash.ToHexString(hashes.WaveletHash));
            AnsiConsole.Write(table);

            if (path2 != null)
            {
                if (!CliOutput.ValidateInputExists(path2)) return;
                using var img2 = FormatRegistry.Read(path2);
                var hashes2 = PerceptualHash.ComputeAll(img2);
                var comparison = PerceptualHash.Compare(hashes, hashes2);

                AnsiConsole.WriteLine();
                var cmpTable = new Table();
                cmpTable.AddColumn("[bold]Hash Type[/]");
                cmpTable.AddColumn("[bold]Distance[/]");
                cmpTable.AddColumn("[bold]Status[/]");
                cmpTable.AddRow("aHash", comparison.AverageDistance.ToString(), comparison.AverageDistance <= 10 ? "[green]SIMILAR[/]" : "[red]DIFFERENT[/]");
                cmpTable.AddRow("dHash", comparison.DifferenceDistance.ToString(), comparison.DifferenceDistance <= 10 ? "[green]SIMILAR[/]" : "[red]DIFFERENT[/]");
                cmpTable.AddRow("pHash", comparison.PerceptualDistance.ToString(), comparison.PerceptualDistance <= 10 ? "[green]SIMILAR[/]" : "[red]DIFFERENT[/]");
                cmpTable.AddRow("wHash", comparison.WaveletDistance.ToString(), comparison.WaveletDistance <= 10 ? "[green]SIMILAR[/]" : "[red]DIFFERENT[/]");
                AnsiConsole.Write(cmpTable);

                AnsiConsole.MarkupLine(comparison.AreSimilar()
                    ? "\n[bold green]Overall: SIMILAR[/]"
                    : "\n[bold red]Overall: DIFFERENT[/]");
            }
        });
        return cmd;
    }

    public static Command CreateHistogramMatchCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Source image file path" };
        var refArg = new Argument<string>("reference") { Description = "Reference image whose color distribution to match" };
        var outputOpt = new Option<string>("-o", "--output") { Description = "Output image file path", Required = true };
        var cmd = new Command("histmatch", """
            Match the color histogram of one image to another (color transfer).
            Adjusts the source image's per-channel color distribution to match the reference.
            Useful for color grading, style transfer, and exposure matching.

            Examples:
              sharpimage histmatch source.png reference.png -o matched.png
            """);
        cmd.Add(inputArg);
        cmd.Add(refArg);
        cmd.Add(outputOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string reference = parseResult.GetValue(refArg)!;
            string output = parseResult.GetValue(outputOpt)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            if (!CliOutput.ValidateInputExists(reference)) return;

            using var refImg = FormatRegistry.Read(reference);
            CliOutput.RunPipeline(input, output, "Histogram matching",
                img => SharpImage.Enhance.HistogramMatching.Apply(img, refImg));
        });
        return cmd;
    }

    public static Command CreateHistogramRenderCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output histogram visualization image path" };
        var cmd = new Command("histogram", """
            Render a color histogram visualization of an image.
            Shows the frequency distribution of pixel intensities per channel
            (Red, Green, Blue) overlaid on a single image.

            Examples:
              sharpimage histogram photo.jpg histogram.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Rendering histogram",
                img => SharpImage.Enhance.HistogramMatching.RenderHistogram(img));
        });
        return cmd;
    }

    public static Command CreateSmartCropCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "Target crop width in pixels", Required = true };
        var heightOpt = new Option<int>("-h", "--height") { Description = "Target crop height in pixels", Required = true };
        var cmd = new Command("smartcrop", """
            Entropy-based smart crop.
            Automatically finds and crops to the most visually interesting
            region of an image using entropy and edge density analysis.
            Unlike center-crop, smart crop intelligently selects the region
            with the most visual information and detail.

            Examples:
              sharpimage smartcrop photo.jpg thumbnail.png -w 400 -h 300
              sharpimage smartcrop landscape.png square.png -w 500 -h 500
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Smart cropping",
                img => SmartCrop.Apply(img, w, h));
        });
        return cmd;
    }

    public static Command CreateInterestMapCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output heatmap file path" };
        var cmd = new Command("interestmap", """
            Visualize the interestingness heatmap used by smart crop.
            Shows which regions of an image have the highest entropy
            and edge density — the areas smart crop considers most important.
            Bright/warm colors indicate high interest; dark/cool colors indicate low interest.

            Examples:
              sharpimage interestmap photo.jpg heatmap.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Building interest map",
                img => SmartCrop.GetInterestMap(img));
        });
        return cmd;
    }

    public static Command CreatePipelineCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var stepsOpt = new Option<string[]>("-s", "--step") { Description = "Pipeline step (repeatable). Each step is an operation with args.", Required = true };
        stepsOpt.AllowMultipleArgumentsPerToken = false;
        var listOpt = new Option<bool>("--list") { Description = "List all available pipeline operations" };

        var cmd = new Command("pipeline", """
            Chain multiple image operations in a single command.
            Each -s/--step specifies one operation with its arguments.
            Operations execute in the order specified.

            Examples:
              sharpimage pipeline photo.jpg result.png -s "resize 800 600" -s "blur 1.5" -s "grayscale"
              sharpimage pipeline input.png output.png -s "crop 10 10 200 200" -s "sepia" -s "vignette 15"
              sharpimage pipeline scan.png clean.png -s "deskew" -s "trim" -s "autolevel"
              sharpimage pipeline --list
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(stepsOpt);
        cmd.Add(listOpt);
        cmd.SetAction((parseResult) =>
        {
            bool showList = parseResult.GetValue(listOpt);
            if (showList)
            {
                var table = new Table();
                table.AddColumn("Operation");
                table.AddColumn("Arguments");
                table.AddColumn("Description");
                foreach (var (name, args, desc) in ImagePipeline.AvailableOperations)
                    table.AddRow(name, args, desc);
                AnsiConsole.Write(table);
                return;
            }

            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string[] steps = parseResult.GetValue(stepsOpt)!;

            if (!CliOutput.ValidateInputExists(input)) return;

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Executing {steps.Length}-step pipeline...", ctx =>
            {
                using var pipeline = ImagePipeline.Execute(input, steps);
                pipeline.Save(output);
            });

            AnsiConsole.MarkupLine($"[green]✓[/] Pipeline complete → [blue]{output}[/] ({steps.Length} steps)");
        });
        return cmd;
    }

    public static Command CreateTensorExportCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output .sitf binary tensor file" };
        var layoutOpt = new Option<string>("--layout") { Description = "Tensor layout: chw (PyTorch) or hwc (TensorFlow)", DefaultValueFactory = _ => "chw" };
        var normOpt = new Option<string>("--normalize") { Description = "Normalization: 01, neg11, imagenet, raw", DefaultValueFactory = _ => "01" };
        var alphaOpt = new Option<bool>("--alpha") { Description = "Include alpha channel" };
        var statsOpt = new Option<bool>("--stats") { Description = "Print per-channel statistics" };

        var cmd = new Command("tensor", """
            Export image as a binary tensor file (.sitf) for ML/AI pipelines.
            Supports CHW (PyTorch) and HWC (TensorFlow) layouts with configurable normalization.

            The SITF format is a simple binary format: 4-byte magic "SITF", version int32,
            3 shape int32s, then float32 tensor data. Easy to memory-map from Python/C++.

            Examples:
              sharpimage tensor photo.jpg output.sitf
              sharpimage tensor photo.jpg output.sitf --layout hwc --normalize imagenet
              sharpimage tensor photo.jpg output.sitf --alpha --stats
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(layoutOpt);
        cmd.Add(normOpt);
        cmd.Add(alphaOpt);
        cmd.Add(statsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string layoutStr = parseResult.GetValue(layoutOpt)!;
            string normStr = parseResult.GetValue(normOpt)!;
            bool alpha = parseResult.GetValue(alphaOpt);
            bool stats = parseResult.GetValue(statsOpt);

            if (!CliOutput.ValidateInputExists(input)) return;

            var layout = layoutStr.ToLowerInvariant() == "hwc"
                ? TensorExport.TensorLayout.HWC
                : TensorExport.TensorLayout.CHW;

            var norm = normStr.ToLowerInvariant() switch
            {
                "neg11" or "-1to1" => TensorExport.NormalizationMode.NegOneToOne,
                "imagenet" => TensorExport.NormalizationMode.ImageNet,
                "raw" => TensorExport.NormalizationMode.Raw,
                _ => TensorExport.NormalizationMode.ZeroToOne
            };

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Exporting tensor...", ctx =>
            {
                using var image = FormatRegistry.Read(input);
                var shape = TensorExport.GetShape(image, layout, alpha);
                TensorExport.SaveBinary(image, output, layout, norm, alpha);

                AnsiConsole.MarkupLine($"[green]✓[/] Tensor saved → [blue]{output}[/]");
                string layoutLabel = layout == TensorExport.TensorLayout.CHW ? "CxHxW" : "HxWxC";
                AnsiConsole.MarkupLine($"  Shape: [bold]{layoutLabel}[/] = {shape.Dim0} x {shape.Dim1} x {shape.Dim2}");
                AnsiConsole.MarkupLine($"  Layout: [bold]{layout}[/]  Normalization: [bold]{norm}[/]");

                if (stats)
                {
                    var channelStats = TensorExport.GetChannelStatistics(image);
                    var table = new Table();
                    table.AddColumn("Channel");
                    table.AddColumn("Min");
                    table.AddColumn("Max");
                    table.AddColumn("Mean");
                    table.AddColumn("Std");
                    foreach (var s in channelStats)
                        table.AddRow(s.Name, $"{s.Min:F4}", $"{s.Max:F4}", $"{s.Mean:F4}", $"{s.Std:F4}");
                    AnsiConsole.Write(table);
                }
            });
        });
        return cmd;
    }

    public static Command CreatePaletteCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output palette swatch image" };
        var countOpt = new Option<int>("-n", "--colors") { Description = "Number of colors to extract (default 8)", DefaultValueFactory = _ => 8 };
        var mapOpt = new Option<string?>("--map") { Description = "Optional: save palette-mapped version to this path" };

        var cmd = new Command("palette", """
            Extract dominant colors from an image using k-means++ clustering.
            Produces a palette swatch image and optionally a palette-mapped version.

            Examples:
              sharpimage palette photo.jpg swatch.png
              sharpimage palette photo.jpg swatch.png -n 5
              sharpimage palette photo.jpg swatch.png --map reduced.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(countOpt);
        cmd.Add(mapOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int count = parseResult.GetValue(countOpt);
            string? mapPath = parseResult.GetValue(mapOpt);

            if (!CliOutput.ValidateInputExists(input)) return;

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Extracting palette...", ctx =>
            {
                using var image = FormatRegistry.Read(input);
                var palette = PaletteExtraction.Extract(image, colorCount: count);

                // Save swatch
                using var swatch = PaletteExtraction.RenderSwatch(palette);
                FormatRegistry.Write(swatch, output);

                // Print palette table
                var table = new Table();
                table.AddColumn("#");
                table.AddColumn("Color");
                table.AddColumn("Hex");
                table.AddColumn("R");
                table.AddColumn("G");
                table.AddColumn("B");
                table.AddColumn("%");

                for (int i = 0; i < palette.Length; i++)
                {
                    var p = palette[i];
                    table.AddRow(
                        (i + 1).ToString(),
                        $"[on {p.ToHex()}]    [/]",
                        p.ToHex(),
                        $"{p.R:F3}",
                        $"{p.G:F3}",
                        $"{p.B:F3}",
                        $"{p.Percentage:F1}%"
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[green]✓[/] Swatch saved → [blue]{output}[/]");

                if (mapPath != null)
                {
                    using var mapped = PaletteExtraction.MapToPalette(image, palette);
                    FormatRegistry.Write(mapped, mapPath);
                    AnsiConsole.MarkupLine($"[green]✓[/] Mapped image saved → [blue]{mapPath}[/]");
                }
            });
        });
        return cmd;
    }

    public static Command CreateDiffCommand()
    {
        var inputAArg = new Argument<string>("imageA") { Description = "First (reference) image" };
        var inputBArg = new Argument<string>("imageB") { Description = "Second (comparison) image" };
        var ssimOpt = new Option<string?>("--ssim") { Description = "Save SSIM heatmap to this path" };
        var deltaEOpt = new Option<string?>("--deltae") { Description = "Save Delta-E heatmap to this path" };
        var thresholdOpt = new Option<double>("--threshold") { Description = "Delta-E threshold for 'changed' (default 2.0)", DefaultValueFactory = _ => 2.0 };

        var cmd = new Command("diff", """
            Perceptual image comparison using SSIM and CIE Delta-E 2000.
            Computes structural similarity and color difference metrics.
            Optionally generates heatmap visualizations.

            Examples:
              sharpimage diff original.png modified.png
              sharpimage diff original.png modified.png --ssim ssim.png --deltae delta.png
              sharpimage diff photo1.jpg photo2.jpg --threshold 5.0
            """);
        cmd.Add(inputAArg);
        cmd.Add(inputBArg);
        cmd.Add(ssimOpt);
        cmd.Add(deltaEOpt);
        cmd.Add(thresholdOpt);
        cmd.SetAction((parseResult) =>
        {
            string inputA = parseResult.GetValue(inputAArg)!;
            string inputB = parseResult.GetValue(inputBArg)!;
            string? ssimPath = parseResult.GetValue(ssimOpt);
            string? deltaEPath = parseResult.GetValue(deltaEOpt);
            double threshold = parseResult.GetValue(thresholdOpt);

            if (!CliOutput.ValidateInputExists(inputA)) return;
            if (!CliOutput.ValidateInputExists(inputB)) return;

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Comparing images...", ctx =>
            {
                using var imageA = FormatRegistry.Read(inputA);
                using var imageB = FormatRegistry.Read(inputB);

                var result = PerceptualDiff.Compare(imageA, imageB,
                    generateSsimMap: ssimPath != null,
                    generateDeltaEMap: deltaEPath != null,
                    deltaEThreshold: threshold);

                var table = new Table();
                table.AddColumn("Metric");
                table.AddColumn("Value");
                table.AddRow("SSIM (mean)", $"{result.MeanSsim:F6}");
                table.AddRow("Delta-E (mean)", $"{result.MeanDeltaE:F3}");
                table.AddRow("Delta-E (max)", $"{result.MaxDeltaE:F3}");
                table.AddRow("Changed pixels", $"{result.PercentChanged:F2}%");
                table.AddRow("Threshold", $"{threshold:F1}");
                AnsiConsole.Write(table);

                if (ssimPath != null && result.SsimMap != null)
                {
                    FormatRegistry.Write(result.SsimMap, ssimPath);
                    AnsiConsole.MarkupLine($"[green]✓[/] SSIM map saved → [blue]{ssimPath}[/]");
                    result.SsimMap.Dispose();
                }

                if (deltaEPath != null && result.DeltaEMap != null)
                {
                    FormatRegistry.Write(result.DeltaEMap, deltaEPath);
                    AnsiConsole.MarkupLine($"[green]✓[/] Delta-E map saved → [blue]{deltaEPath}[/]");
                    result.DeltaEMap.Dispose();
                }
            });
        });
        return cmd;
    }

    public static Command CreateAnimWebpCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input animated GIF or multi-frame image" };
        var outputArg = new Argument<string>("output") { Description = "Output animated WebP file" };
        var qualityOpt = new Option<int>("--quality") { Description = "Quality (100=lossless, <100=lossy, default 100)", DefaultValueFactory = _ => 100 };
        var delayOpt = new Option<int?>("--delay") { Description = "Override frame delay in centiseconds" };

        var cmd = new Command("animwebp", """
            Convert animated GIF (or any multi-frame source) to animated WebP.
            Preserves frame timing, disposal methods, and transparency.

            Examples:
              sharpimage animwebp animation.gif output.webp
              sharpimage animwebp animation.gif output.webp --quality 80
              sharpimage animwebp animation.gif output.webp --delay 5
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(qualityOpt);
        cmd.Add(delayOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int quality = parseResult.GetValue(qualityOpt);
            int? delay = parseResult.GetValue(delayOpt);

            if (!CliOutput.ValidateInputExists(input)) return;

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start("Converting to animated WebP...", ctx =>
            {
                using var sequence = FormatRegistry.ReadSequence(input);

                if (delay.HasValue)
                {
                    foreach (var frame in sequence.Frames)
                        frame.Delay = delay.Value;
                }

                using var stream = new FileStream(output, FileMode.Create, FileAccess.Write);
                WebpCoder.WriteSequence(sequence, stream, quality);
                AnsiConsole.MarkupLine($"[green]✓[/] Animated WebP saved → [blue]{output}[/] ({sequence.Count} frames)");
            });
        });
        return cmd;
    }

    public static Command CreateModernDitherCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var methodOpt = new Option<string>("-m", "--method") { Description = "Dithering method: bluenoise, stucki, atkinson, sierra", DefaultValueFactory = _ => "bluenoise" };
        var levelsOpt = new Option<int>("-l", "--levels") { Description = "Number of quantization levels (2 = black/white, 4 = 4 shades)", DefaultValueFactory = _ => 2 };
        var cmd = new Command("moderndither", """
            Apply modern dithering algorithms to an image.
            Reduces color depth while maintaining visual quality using advanced
            error distribution or spatial noise patterns.

            Methods:
              bluenoise  - R2-sequence threshold dithering (no visible patterns)
              stucki     - Error diffusion with 12-neighbor kernel (sharp detail)
              atkinson   - Error diffusion distributing only 75% of error (lighter output)
              sierra     - Sierra two-row error diffusion (balanced quality)

            Examples:
              sharpimage dither photo.jpg dithered.png
              sharpimage dither photo.jpg dithered.png -m stucki
              sharpimage dither photo.jpg dithered.png -m atkinson -l 4
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(methodOpt);
        cmd.Add(levelsOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string method = parseResult.GetValue(methodOpt)!;
            int levels = parseResult.GetValue(levelsOpt);

            if (!CliOutput.ValidateInputExists(input)) return;

            AnsiConsole.Status().Spinner(Spinner.Known.Dots).Start($"Applying {method} dithering...", ctx =>
            {
                using var image = FormatRegistry.Read(input);
                switch (method.ToLowerInvariant())
                {
                    case "bluenoise": ThresholdOps.BlueNoiseDither(image, levels); break;
                    case "stucki": ThresholdOps.StuckiDither(image, levels); break;
                    case "atkinson": ThresholdOps.AtkinsonDither(image, levels); break;
                    case "sierra": ThresholdOps.SierraDither(image, levels); break;
                    default:
                        AnsiConsole.MarkupLine($"[red]Unknown method:[/] {method}. Use bluenoise, stucki, atkinson, or sierra.");
                        return;
                }
                FormatRegistry.Write(image, output);
                AnsiConsole.MarkupLine($"[green]✓[/] {method} dithered → [blue]{output}[/] ({levels} levels)");
            });
        });
        return cmd;
    }
}
