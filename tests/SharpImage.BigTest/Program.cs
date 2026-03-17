using Spectre.Console;
using System.Diagnostics;
using System.Text;

namespace SharpImage.BigTest;

/// <summary>
/// Comprehensive visual and technical validation of every SharpImage CLI function. Runs each operation with both
/// SharpImage CLI and ImageMagick, collects results, measures performance, and generates a summary report.
/// </summary>
public static class Program
{
    // Paths
    static readonly string Root = FindRepoRoot();
    static string ImagesDir => Path.Combine(AppContext.BaseDirectory, "TestAssets");
    static string BigTestDir => Path.Combine(AppContext.BaseDirectory, "BigTestOutput");
    static string SharpImageCli => Path.Combine(Root, "src", "SharpImage.Cli", "SharpImage.Cli.csproj");
    static string SharpImageAotDir => Path.Combine(Root, "publish", "aot");
    static string SharpImageExe => Path.Combine(SharpImageAotDir, "SharpImage.Cli.exe");
    static string MagickExe => Path.Combine(Root, "tools", "imagemagick", "magick.exe");

    // Source images (will be copied to BigTest, never modified)
    static string SourcePng => Path.Combine(ImagesDir, "scene.png");
    static string SourceJpg => Path.Combine(ImagesDir, "photo_small.jpg");
    static string SourceSmallPng => Path.Combine(ImagesDir, "photo_small.png");
    static string SourceMountains => Path.Combine(ImagesDir, "landscape.jpg");
    static string SourceGranite => Path.Combine(ImagesDir, "texture_pattern.png");
    static string SourceWizard => Path.Combine(ImagesDir, "peppers_rgba.png");

    static readonly List<TestResult> results = [];
    static readonly List<PerfResult> perfResults = [];

    public static async Task<int> Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("Big Test").Color(Color.DodgerBlue2));
        AnsiConsole.MarkupLine("[bold]SharpImage Comprehensive Validation Suite[/]");
        AnsiConsole.MarkupLine($"[dim]SharpImage CLI: {SharpImageExe} (AOT)[/]");
        AnsiConsole.MarkupLine($"[dim]ImageMagick:    {MagickExe}[/]");
        AnsiConsole.WriteLine();

        // Build SharpImage CLI as AOT Release for fair comparison
        AnsiConsole.MarkupLine("[yellow]Publishing SharpImage CLI (AOT Release)...[/]");
        var (buildExit, _, buildErr) = await RunProcess("dotnet",
            $"publish \"{SharpImageCli}\" -c Release -r win-x64 --self-contained -p:PublishAot=true -o \"{SharpImageAotDir}\" -v quiet",
            300);
        if (buildExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]AOT publish failed: {buildErr}[/]");
            return 1;
        }
        AnsiConsole.MarkupLine("[green]AOT publish succeeded.[/]");

        await RunAllTests();

        PrintSummary();
        WritePerfCsv();

        return results.Any(r => !r.Passed) ? 1 : 0;
    }

    /// <summary>Ensures the directory for the given file path exists. Call before any File.Copy or write.</summary>
    static void EnsureDirectoryFor(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
    }

    static async Task RunAllTests()
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var convTask = ctx.AddTask("[cyan]Format Conversions[/]", maxValue: 11);
                await RunFormatConversions(convTask);

                var rawTask = ctx.AddTask("[indianred1]Camera Raw Decode[/]", maxValue: 7);
                await RunCameraRawDecodes(rawTask);

                var transTask = ctx.AddTask("[green]Transform Operations[/]", maxValue: 40);
                await RunTransformOperations(transTask);

                var fxTask = ctx.AddTask("[yellow]Effects Operations[/]", maxValue: 44);
                await RunEffectsOperations(fxTask);

                var advTask = ctx.AddTask("[magenta]Advanced Operations[/]", maxValue: 13);
                await RunAdvancedOperations(advTask);

                var analysisTask = ctx.AddTask("[red]Analysis & Detection[/]", maxValue: 5);
                await RunAnalysisOperations(analysisTask);

                var channelTask = ctx.AddTask("[cyan3]Channel Operations[/]", maxValue: 3);
                await RunChannelOperations(channelTask);

                var metadataTask = ctx.AddTask("[wheat1]Metadata Operations[/]", maxValue: 3);
                await RunMetadataOperations(metadataTask);

                var csTask = ctx.AddTask("[mediumpurple1]Colorspace Round-Trips[/]", maxValue: 6);
                await RunColorspaceOperations(csTask);

                var novelTask = ctx.AddTask("[springgreen3_1]Novel Features[/]", maxValue: 5);
                await RunNovelFeatures(novelTask);

                var mathVisTask = ctx.AddTask("[lightsalmon3]Math & Visual Ops[/]", maxValue: 10);
                await RunMathVisualOperations(mathVisTask);

                var utilTask = ctx.AddTask("[steelblue1]Utility Operations[/]", maxValue: 7);
                await RunUtilityOperations(utilTask);

                var alphaTask = ctx.AddTask("[darkorange]Alpha & Pixel Ops[/]", maxValue: 8);
                await RunAlphaPixelOperations(alphaTask);

                var phase49Task = ctx.AddTask("[plum1]Pixel & Threshold Ops[/]", maxValue: 9);
                await RunPhase49Operations(phase49Task);

                var perfTask = ctx.AddTask("[blue]Performance Comparison[/]", maxValue: 16);
                await RunPerformanceComparison(perfTask);
            });
    }

    // ─────────────────────────────────────────────────────────────
    // 1. FORMAT CONVERSIONS
    // ─────────────────────────────────────────────────────────────

    static readonly string[] ConvertFormats = [ "png", "jpg", "bmp", "tiff", "webp", "gif", "tga", "pnm", "qoi", "ff", "pcx" ];
    static readonly Dictionary<string, string> FormatLabels = new()
    {
        ["png"] = "PNG", ["jpg"] = "JPEG", ["bmp"] = "BMP", ["tiff"] = "TIFF",
        ["webp"] = "WebP", ["gif"] = "GIF", ["tga"] = "TGA", ["pnm"] = "PNM",
        ["qoi"] = "QOI", ["ff"] = "Farbfeld", ["pcx"] = "PCX"
    };

    // Map our extensions to ImageMagick-compatible extensions
    static readonly Dictionary<string, string> ImExtMap = new()
    {
        ["png"] = "png", ["jpg"] = "jpg", ["bmp"] = "bmp", ["tiff"] = "tiff",
        ["webp"] = "webp", ["gif"] = "gif", ["tga"] = "tga", ["pnm"] = "pnm",
        ["qoi"] = "qoi", ["ff"] = "ff", ["pcx"] = "pcx"
    };

    static async Task RunFormatConversions(ProgressTask task)
    {
        // For each source format, create a source file, then convert to all others
        foreach (var srcFmt in ConvertFormats)
        {
            string label = FormatLabels[srcFmt];
            string subDir = Path.Combine(BigTestDir, "Convert", label);

            // Create source file in this format
            string sourceFile = Path.Combine(subDir, $"source.{srcFmt}");
            // Convert photo_small.png to the source format using SharpImage
            if (srcFmt == "png")
            {
                EnsureDirectoryFor(sourceFile);
                File.Copy(SourceSmallPng, sourceFile, true);
            }
            else
            {
                EnsureDirectoryFor(sourceFile);
                await RunSharpImage($"convert \"{SourceSmallPng}\" \"{sourceFile}\"");
            }

            // Convert to every other format
            foreach (var dstFmt in ConvertFormats)
            {
                if (dstFmt == srcFmt)
                {
                    continue;
                }

                string siOut = Path.Combine(subDir, $"si_to_{FormatLabels[dstFmt]}.{dstFmt}");
                string imOut = Path.Combine(subDir, $"im_to_{FormatLabels[dstFmt]}.{dstFmt}");

                // SharpImage convert
                var siResult = await RunSharpImage($"convert \"{sourceFile}\" \"{siOut}\"");
                bool siOk = siResult.ExitCode == 0 && File.Exists(siOut) && new FileInfo(siOut).Length > 0;

                // ImageMagick convert (skip formats IM doesn't support natively: qoi, ff)
                bool imOk = false;
                string imExt = dstFmt == "ff" ? "farbfeld" : ImExtMap.GetValueOrDefault(dstFmt, dstFmt);
                // IM uses different extension for farbfeld; skip unsupported
                bool imSupported = dstFmt != "qoi" && dstFmt != "ff";
                string imSrcExt = srcFmt == "ff" ? "farbfeld" : srcFmt;
                bool imSrcSupported = srcFmt != "qoi" && srcFmt != "ff";

                if (imSupported && imSrcSupported)
                {
                    var imResult = await RunMagick($"\"{sourceFile}\" \"{imOut}\"");
                    imOk = imResult.ExitCode == 0 && File.Exists(imOut) && new FileInfo(imOut).Length > 0;
                }

                results.Add(new TestResult
                {
                    Category = "Convert",
                    Name = $"{label} → {FormatLabels[dstFmt]}",
                    SharpImagePassed = siOk,
                    ImageMagickPassed = imOk || !imSupported || !imSrcSupported,
                    SharpImageFile = siOut,
                    ImageMagickFile = imOut,
                    Notes = !imSupported || !imSrcSupported ? "IM: format not natively supported" : ""
                });
            }
            task.Increment(1);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1b. CAMERA RAW DECODE
    // ─────────────────────────────────────────────────────────────

    static readonly (string file, string label)[] CameraRawFiles =
    [
        ("sample1.cr2", "CR2 (Canon)"),
        ("sample1.dng", "DNG (Adobe)"),
        ("sample1.nef", "NEF (Nikon)"),
        ("sample1.orf", "ORF (Olympus)"),
        ("sample1.pef", "PEF (Pentax)"),
        ("sample1.raf", "RAF (Fuji)"),
        ("sample1.rw2", "RW2 (Panasonic)")
    ];

    static async Task RunCameraRawDecodes(ProgressTask task)
    {
        string outDir = Path.Combine(BigTestDir, "CameraRaw");
        Directory.CreateDirectory(outDir);

        foreach (var (file, label) in CameraRawFiles)
        {
            string srcPath = Path.Combine(ImagesDir, file);
            bool siOk = false;
            bool imOk = false;
            string notes = "";

            string ext = Path.GetExtension(file).TrimStart('.');
            string siOut = Path.Combine(outDir, $"si_{ext}_{Path.GetFileNameWithoutExtension(file)}.png");
            string imOut = Path.Combine(outDir, $"im_{ext}_{Path.GetFileNameWithoutExtension(file)}.png");

            if (!File.Exists(srcPath))
            {
                notes = "Source file not found";
            }
            else
            {
                // SharpImage: convert raw → PNG via CLI
                var siResult = await RunSharpImage($"convert \"{srcPath}\" \"{siOut}\"");
                siOk = siResult.ExitCode == 0 && File.Exists(siOut) && new FileInfo(siOut).Length > 0;
                if (!siOk && siResult.ExitCode != 0)
                    notes = $"SI exit={siResult.ExitCode}";

                // ImageMagick: convert raw → PNG
                var imResult = await RunMagick($"\"{srcPath}\" \"{imOut}\"");
                imOk = imResult.ExitCode == 0 && File.Exists(imOut) && new FileInfo(imOut).Length > 0;
            }

            results.Add(new TestResult
            {
                Category = "CameraRaw",
                Name = $"Decode {label}",
                SharpImagePassed = siOk,
                ImageMagickPassed = imOk,
                SharpImageFile = siOut,
                ImageMagickFile = imOut,
                Notes = notes
            });
            task.Increment(1);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2. TRANSFORM OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunTransformOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Transform");
        string src = Path.Combine(dir, "Resize", "source.png");
        EnsureDirectoryFor(src);
        File.Copy(SourcePng, src, true);

        // Also copy to other transform subdirs
        foreach (var sub in new[] { "Crop", "Rotate", "Flip", "Flop", "Distort" })
        {
            var dest = Path.Combine(dir, sub, "source.png");
            EnsureDirectoryFor(dest);
            File.Copy(SourcePng, dest, true);
        }

        // Resize operations
        var resizeDir = Path.Combine(dir, "Resize");
        foreach (var method in new[] { "nearest", "bilinear", "bicubic", "lanczos" })
        {
            string siOut = Path.Combine(resizeDir, $"si_{method}_320x240.png");
            string imOut = Path.Combine(resizeDir, $"im_{method}_320x240.png");

            await RunSharpImage($"resize \"{src}\" \"{siOut}\" -w 320 -h 240 -m {method}");
            string imFilter = method switch
            {
                "nearest" => "Point",
                "bilinear" => "Triangle",
                "bicubic" => "Catrom",
                "lanczos" => "Lanczos",
                _ => method
            };
            await RunMagick($"\"{src}\" -filter {imFilter} -resize 320x240! \"{imOut}\"");

            RecordTransformResult("Resize", $"{method} 320×240", siOut, imOut);
        }
        task.Increment(1);

        // Resize upscale
        {
            string siOut = Path.Combine(resizeDir, "si_lanczos_1280x960.png");
            string imOut = Path.Combine(resizeDir, "im_lanczos_1280x960.png");
            await RunSharpImage($"resize \"{src}\" \"{siOut}\" -w 1280 -h 960 -m lanczos");
            await RunMagick($"\"{src}\" -filter Lanczos -resize 1280x960! \"{imOut}\"");
            RecordTransformResult("Resize", "lanczos upscale 1280×960", siOut, imOut);
        }
        task.Increment(1);

        // ── Group 5: Additional Resize Filters ────────────────────
        foreach (var (siMethod, imFilter, label) in new[]
        {
            ("mitchell",  "Mitchell",  "Mitchell"),
            ("hermite",   "Hermite",   "Hermite"),
            ("gaussian",  "Gaussian",  "Gaussian"),
            ("spline",    "Spline",    "Spline"),
            ("sinc",      "Sinc",      "Sinc"),
            ("lanczos2",  "Lanczos2",  "Lanczos2"),
        })
        {
            string siOut = Path.Combine(resizeDir, $"si_{siMethod}_320x240.png");
            string imOut = Path.Combine(resizeDir, $"im_{siMethod}_320x240.png");
            await RunSharpImage($"resize \"{src}\" \"{siOut}\" -w 320 -h 240 -m {siMethod}");
            await RunMagick($"\"{src}\" -filter {imFilter} -resize 320x240! \"{imOut}\"");
            RecordTransformResult("Resize", $"{label} 320×240", siOut, imOut);
        }
        task.Increment(6);

        // Crop
        {
            var cropDir = Path.Combine(dir, "Crop");
            string cropSrc = Path.Combine(cropDir, "source.png");
            string siOut = Path.Combine(cropDir, "si_crop_200x200.png");
            string imOut = Path.Combine(cropDir, "im_crop_200x200.png");
            await RunSharpImage($"crop \"{cropSrc}\" \"{siOut}\" -x 100 -y 100 -w 200 -h 200");
            await RunMagick($"\"{cropSrc}\" -crop 200x200+100+100 +repage \"{imOut}\"");
            RecordTransformResult("Crop", "200×200 from (100,100)", siOut, imOut);
        }
        task.Increment(1);

        // Rotations
        var rotDir = Path.Combine(dir, "Rotate");
        string rotSrc = Path.Combine(rotDir, "source.png");
        foreach (var angle in new[] { 90, 180, 270, 45 })
        {
            string siOut = Path.Combine(rotDir, $"si_rotate_{angle}.png");
            string imOut = Path.Combine(rotDir, $"im_rotate_{angle}.png");
            await RunSharpImage($"rotate \"{rotSrc}\" \"{siOut}\" -a {angle}");
            await RunMagick($"\"{rotSrc}\" -rotate {angle} \"{imOut}\"");
            RecordTransformResult("Rotate", $"{angle}°", siOut, imOut);
        }
        task.Increment(4);

        // Flip
        {
            var flipDir = Path.Combine(dir, "Flip");
            string flipSrc = Path.Combine(flipDir, "source.png");
            string siOut = Path.Combine(flipDir, "si_flip.png");
            string imOut = Path.Combine(flipDir, "im_flip.png");
            await RunSharpImage($"flip \"{flipSrc}\" \"{siOut}\"");
            await RunMagick($"\"{flipSrc}\" -flip \"{imOut}\"");
            RecordTransformResult("Flip", "vertical mirror", siOut, imOut);
        }
        task.Increment(1);

        // Flop
        {
            var flopDir = Path.Combine(dir, "Flop");
            string flopSrc = Path.Combine(flopDir, "source.png");
            string siOut = Path.Combine(flopDir, "si_flop.png");
            string imOut = Path.Combine(flopDir, "im_flop.png");
            await RunSharpImage($"flop \"{flopSrc}\" \"{siOut}\"");
            await RunMagick($"\"{flopSrc}\" -flop \"{imOut}\"");
            RecordTransformResult("Flop", "horizontal mirror", siOut, imOut);
        }
        task.Increment(1);

        // Distort barrel
        {
            var distDir = Path.Combine(dir, "Distort");
            string distSrc = Path.Combine(distDir, "source.png");
            string siOut = Path.Combine(distDir, "si_barrel.png");
            string imOut = Path.Combine(distDir, "im_barrel.png");
            await RunSharpImage($"distort \"{distSrc}\" \"{siOut}\" -t barrel -a 0.3 -b 0.0 -c 0.0 -d 0.7");
            await RunMagick($"\"{distSrc}\" -distort Barrel \"0.3 0.0 0.0 0.7\" \"{imOut}\"");
            RecordTransformResult("Distort", "barrel k1=0.3", siOut, imOut);
        }
        task.Increment(1);

        // ── Group 4: Geometry & Transform ─────────────────────────
        // Use peppers_rgba.png for these — it has transparency ideal for trim
        string wizSrc = SourceWizard;

        // Trim
        {
            var trimDir = Path.Combine(dir, "Trim");
            string localSrc = Path.Combine(trimDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(trimDir, "si_trim.png");
            string imOut = Path.Combine(trimDir, "im_trim.png");
            await RunSharpImage($"trim \"{localSrc}\" \"{siOut}\"");
            await RunMagick($"\"{localSrc}\" -trim +repage \"{imOut}\"");
            RecordTransformResult("Trim", "auto-trim borders", siOut, imOut);
        }
        task.Increment(1);

        // Extent — extend canvas to 1400x1700, center image at (148,116)
        {
            var extDir = Path.Combine(dir, "Extent");
            string localSrc = Path.Combine(extDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(extDir, "si_extent.png");
            string imOut = Path.Combine(extDir, "im_extent.png");
            await RunSharpImage($"extent \"{localSrc}\" \"{siOut}\" -w 1400 -h 1700 -x 148 -y 116");
            await RunMagick($"\"{localSrc}\" -background black -gravity Center -extent 1400x1700 \"{imOut}\"");
            RecordTransformResult("Extent", "1400×1700 padded", siOut, imOut);
        }
        task.Increment(1);

        // Shave — remove 50px from each edge
        {
            var shaveDir = Path.Combine(dir, "Shave");
            string localSrc = Path.Combine(shaveDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(shaveDir, "si_shave.png");
            string imOut = Path.Combine(shaveDir, "im_shave.png");
            await RunSharpImage($"shave \"{localSrc}\" \"{siOut}\" --horizontal 50 --vertical 50");
            await RunMagick($"\"{localSrc}\" -shave 50x50 \"{imOut}\"");
            RecordTransformResult("Shave", "50px all edges", siOut, imOut);
        }
        task.Increment(1);

        // Chop — remove 200x300 region at (200,300)
        {
            var chopDir = Path.Combine(dir, "Chop");
            string localSrc = Path.Combine(chopDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(chopDir, "si_chop.png");
            string imOut = Path.Combine(chopDir, "im_chop.png");
            await RunSharpImage($"chop \"{localSrc}\" \"{siOut}\" -x 200 -y 300 -w 200 -h 300");
            await RunMagick($"\"{localSrc}\" -chop 200x300+200+300 \"{imOut}\"");
            RecordTransformResult("Chop", "200×300 at (200,300)", siOut, imOut);
        }
        task.Increment(1);

        // Transpose
        {
            var tpDir = Path.Combine(dir, "Transpose");
            string localSrc = Path.Combine(tpDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(tpDir, "si_transpose.png");
            string imOut = Path.Combine(tpDir, "im_transpose.png");
            await RunSharpImage($"transpose \"{localSrc}\" \"{siOut}\"");
            await RunMagick($"\"{localSrc}\" -transpose \"{imOut}\"");
            RecordTransformResult("Transpose", "diagonal mirror", siOut, imOut);
        }
        task.Increment(1);

        // Transverse
        {
            var tvDir = Path.Combine(dir, "Transverse");
            string localSrc = Path.Combine(tvDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string siOut = Path.Combine(tvDir, "si_transverse.png");
            string imOut = Path.Combine(tvDir, "im_transverse.png");
            await RunSharpImage($"transverse \"{localSrc}\" \"{siOut}\"");
            await RunMagick($"\"{localSrc}\" -transverse \"{imOut}\"");
            RecordTransformResult("Transverse", "anti-diagonal mirror", siOut, imOut);
        }
        task.Increment(1);

        // --- Deskew ---
        {
            var deskDir = Path.Combine(dir, "Deskew");
            string localSrc = Path.Combine(deskDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(wizSrc, localSrc, true);

            string skewedSrc = Path.Combine(deskDir, "skewed_source.png");
            await RunMagick($"\"{localSrc}\" -background white -rotate 5 \"{skewedSrc}\"");
            string siOut = Path.Combine(deskDir, "si_deskew.png");
            string imOut = Path.Combine(deskDir, "im_deskew.png");
            await RunSharpImage($"deskew \"{skewedSrc}\" \"{siOut}\"");
            await RunMagick($"\"{skewedSrc}\" -deskew 40% \"{imOut}\"");
            RecordTransformResult("Deskew", "auto-straighten 5° skew", siOut, imOut);
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 3. EFFECTS OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunEffectsOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Effects");
        string src = SourcePng; // scene.png 1280×853

        // Helper to copy source and run effect
        async Task RunEffect(string subDir, string testName, string siArgs, string imArgs)
        {
            string effectDir = Path.Combine(dir, subDir);
            string localSrc = Path.Combine(effectDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string safeName = testName.Replace(" ", "_").Replace("%", "pct").Replace("=", "");
            string siOut = Path.Combine(effectDir, $"si_{safeName}.png");
            string imOut = Path.Combine(effectDir, $"im_{safeName}.png");

            await RunSharpImage($"{siArgs.Replace("{SRC}", $"\"{localSrc}\"").Replace("{OUT}", $"\"{siOut}\"")}");

            if (!string.IsNullOrEmpty(imArgs))
            {
                await RunMagick($"\"{localSrc}\" {imArgs} \"{imOut}\"");
            }

            RecordTransformResult(subDir, testName, siOut, imOut);
            task.Increment(1);
        }

        // Blur
        await RunEffect("Blur", "gaussian_s1", "blur {SRC} {OUT} -s 1.0", "-gaussian-blur 0x1");
        await RunEffect("Blur", "gaussian_s3", "blur {SRC} {OUT} -s 3.0", "-gaussian-blur 0x3");
        await RunEffect("Blur", "box", "blur {SRC} {OUT} --box -s 2.0", "-blur 2x2");

        // Sharpen
        await RunEffect("Sharpen", "sharpen", "sharpen {SRC} {OUT} -s 1.0 -a 1.5", "-sharpen 0x1");

        // Edge
        await RunEffect("Edge", "edge", "edge {SRC} {OUT}", "-edge 1");

        // Emboss
        await RunEffect("Emboss", "emboss", "emboss {SRC} {OUT}", "-emboss 1");

        // Brightness
        await RunEffect("Brightness", "bright_up", "brightness {SRC} {OUT} -f 1.5", "-modulate 150,100,100");
        await RunEffect("Brightness", "bright_down", "brightness {SRC} {OUT} -f 0.7", "-modulate 70,100,100");

        // Contrast
        await RunEffect("Contrast", "contrast_up", "contrast {SRC} {OUT} -f 1.5", "-brightness-contrast 0x50");
        await RunEffect("Contrast", "contrast_down", "contrast {SRC} {OUT} -f 0.7", "-brightness-contrast 0x-30");

        // Gamma
        await RunEffect("Gamma", "gamma_05", "gamma {SRC} {OUT} -v 0.5", "-gamma 0.5");
        await RunEffect("Gamma", "gamma_20", "gamma {SRC} {OUT} -v 2.0", "-gamma 2.0");

        // Grayscale
        await RunEffect("Grayscale", "gray", "grayscale {SRC} {OUT}", "-colorspace Gray");

        // Invert
        await RunEffect("Invert", "invert", "invert {SRC} {OUT}", "-negate");

        // Threshold
        await RunEffect("Threshold", "thresh_50", "threshold {SRC} {OUT} -v 0.5", "-threshold 50%");

        // Posterize
        await RunEffect("Posterize", "post_4", "posterize {SRC} {OUT} -l 4", "-posterize 4");

        // Saturate
        await RunEffect("Saturate", "sat_150", "saturate {SRC} {OUT} -f 1.5", "-modulate 100,150,100");
        await RunEffect("Saturate", "sat_50", "saturate {SRC} {OUT} -f 0.5", "-modulate 100,50,100");

        // Levels
        await RunEffect("Levels", "levels", "levels {SRC} {OUT} --in-black 0.2 --in-white 0.8", "-level 20%,80%");

        // Enhancement operations (Group 1)
        await RunEffect("Enhance", "equalize", "equalize {SRC} {OUT}", "-equalize");
        await RunEffect("Enhance", "normalize", "normalize {SRC} {OUT}", "-normalize");
        await RunEffect("Enhance", "autolevel", "autolevel {SRC} {OUT}", "-auto-level");
        await RunEffect("Enhance", "autogamma", "autogamma {SRC} {OUT}", "-auto-gamma");
        await RunEffect("Enhance", "sigmoidal", "sigmoidal {SRC} {OUT} -c 5 -m 0.5", "-sigmoidal-contrast 5x50%");
        await RunEffect("Enhance", "clahe", "clahe {SRC} {OUT} --tiles-x 8 --tiles-y 8 --clip 2.0", "-clahe 8x8+2+0");
        await RunEffect("Enhance", "modulate", "modulate {SRC} {OUT} -b 120 -s 130", "-modulate 120,130,100");
        await RunEffect("Enhance", "whitebalance", "whitebalance {SRC} {OUT}", "-white-balance");
        await RunEffect("Enhance", "colorize", "colorize {SRC} {OUT} -r 255 -g 200 -b 100 -a 0.3", "-fill rgb(255,200,100) -colorize 30%");
        await RunEffect("Enhance", "solarize", "solarize {SRC} {OUT} -t 0.5", "-solarize 50%");
        await RunEffect("Enhance", "sepia", "sepia {SRC} {OUT} -t 0.8", "-sepia-tone 80%");

        // Blur & Noise operations (Group 2)
        await RunEffect("BlurNoise", "motionblur", "motionblur {SRC} {OUT} -r 10 -s 3.0 -a 30", "-motion-blur 0x3+30");
        await RunEffect("BlurNoise", "radialblur", "radialblur {SRC} {OUT} -a 10", "-rotational-blur 10");
        await RunEffect("BlurNoise", "selectiveblur", "selectiveblur {SRC} {OUT} -r 3 -s 2 -t 0.15", "-selective-blur 0x2+15%");
        await RunEffect("BlurNoise", "noise_gaussian", "noise {SRC} {OUT} -t gaussian -a 1.0", "-attenuate 1 +noise Gaussian");
        await RunEffect("BlurNoise", "noise_impulse", "noise {SRC} {OUT} -t impulse -a 1.0", "-attenuate 1 +noise Impulse");
        await RunEffect("BlurNoise", "despeckle", "despeckle {SRC} {OUT}", "-despeckle");
        await RunEffect("BlurNoise", "waveletdenoise", "waveletdenoise {SRC} {OUT} -t 0.3", "-wavelet-denoise 30%");

        // Artistic effects
        await RunEffect("Artistic", "oilpaint", "oilpaint {SRC} {OUT} -r 3", "-paint 3");
        await RunEffect("Artistic", "charcoal", "charcoal {SRC} {OUT} -r 1 -s 0.5", "-charcoal 1x0.5");
        await RunEffect("Artistic", "sketch", "sketch {SRC} {OUT} -s 0.5 -a 45", "-sketch 0x0.5+45");
        await RunEffect("Artistic", "vignette", "vignette {SRC} {OUT} -s 10", "-vignette 0x10");
        await RunEffect("Artistic", "wave", "wave {SRC} {OUT} -a 10 -w 50", "-wave 10x50");
        await RunEffect("Artistic", "swirl", "swirl {SRC} {OUT} -d 60", "-swirl 60");
        await RunEffect("Artistic", "implode", "implode {SRC} {OUT} -a 0.5", "-implode 0.5");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. ADVANCED OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunAdvancedOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Advanced");
        string src = SourcePng;

        // Morphology
        foreach (var op in new[] { "erode", "dilate", "open", "close" })
        {
            string morphDir = Path.Combine(dir, "Morphology");
            string localSrc = Path.Combine(morphDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut = Path.Combine(morphDir, $"si_{op}.png");
            string imOut = Path.Combine(morphDir, $"im_{op}.png");

            await RunSharpImage($"morphology \"{localSrc}\" \"{siOut}\" --op {op} --kernel square --size 3");
            string imOp = $"{char.ToUpper(op[0])}{op[1..]}";
            await RunMagick($"\"{localSrc}\" -morphology {imOp} Square:3 \"{imOut}\"");
            RecordTransformResult("Morphology", op, siOut, imOut);
            task.Increment(1);
        }

        // Quantize
        {
            string qDir = Path.Combine(dir, "Quantize");
            string localSrc = Path.Combine(qDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut16 = Path.Combine(qDir, "si_q16.png");
            string imOut16 = Path.Combine(qDir, "im_q16.png");
            await RunSharpImage($"quantize \"{localSrc}\" \"{siOut16}\" -c 16");
            await RunMagick($"\"{localSrc}\" -colors 16 \"{imOut16}\"");
            RecordTransformResult("Quantize", "16 colors", siOut16, imOut16);

            string siOut8f = Path.Combine(qDir, "si_q8_floyd.png");
            string imOut8f = Path.Combine(qDir, "im_q8_floyd.png");
            await RunSharpImage($"quantize \"{localSrc}\" \"{siOut8f}\" -c 8 --dither floyd");
            await RunMagick($"\"{localSrc}\" -colors 8 -dither FloydSteinberg \"{imOut8f}\"");
            RecordTransformResult("Quantize", "8 colors Floyd-Steinberg", siOut8f, imOut8f);
        }
        task.Increment(2);

        // Ordered Dither
        {
            string dDir = Path.Combine(dir, "Dither");
            string localSrc = Path.Combine(dDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut = Path.Combine(dDir, "si_ordered_4.png");
            string imOut = Path.Combine(dDir, "im_ordered_4.png");
            await RunSharpImage($"dither \"{localSrc}\" \"{siOut}\" --order 4 -l 4");
            await RunMagick($"\"{localSrc}\" -ordered-dither o4x4 \"{imOut}\"");
            RecordTransformResult("Dither", "ordered 4×4", siOut, imOut);
        }
        task.Increment(1);

        // Composite
        {
            string cDir = Path.Combine(dir, "Composite");
            string baseSrc = Path.Combine(cDir, "base.png");
            string overSrc = Path.Combine(cDir, "overlay.png");
            EnsureDirectoryFor(baseSrc);
            File.Copy(SourceGranite, baseSrc, true);

            EnsureDirectoryFor(overSrc);
            File.Copy(SourceSmallPng, overSrc, true);

            // SI mode name → IM -compose name
            var compositeModes = new (string si, string im)[]
            {
                ("over", "Over"), ("multiply", "Multiply"), ("screen", "Screen"),
                ("darken", "Darken"), ("lighten", "Lighten"),
                ("colordodge", "ColorDodge"), ("colorburn", "ColorBurn"),
                ("hardlight", "Hard_Light"), ("softlight", "Soft_Light"),
                ("difference", "Difference"), ("exclusion", "Exclusion"),
                ("atop", "Atop"), ("xor", "Xor"),
                ("plus", "Plus"), ("minus", "Minus"),
                ("bumpmap", "Bumpmap"),
                ("linearlight", "Linear_Light"), ("vividlight", "Vivid_Light"),
                ("pinlight", "Pin_Light"),
            };
            foreach (var (mode, imMode) in compositeModes)
            {
                string siOut = Path.Combine(cDir, $"si_{mode}.png");
                string imOut = Path.Combine(cDir, $"im_{mode}.png");
                await RunSharpImage($"composite \"{baseSrc}\" \"{overSrc}\" \"{siOut}\" -m {mode}");
                await RunMagick($"\"{baseSrc}\" \"{overSrc}\" -compose {imMode} -composite \"{imOut}\"");
                RecordTransformResult("Composite", mode, siOut, imOut);
            }
        }
        task.Increment(19);

        // Compare
        {
            string cmpDir = Path.Combine(dir, "Compare");
            string imgA = Path.Combine(cmpDir, "imageA.png");
            string imgB = Path.Combine(cmpDir, "imageB.png");
            EnsureDirectoryFor(imgA);
            File.Copy(SourceSmallPng, imgA, true);
            // Create a slightly different image B by converting through JPEG (lossy)
            await RunSharpImage($"convert \"{SourceSmallPng}\" \"{Path.Combine(cmpDir, "temp.jpg")}\"");
            await RunSharpImage($"convert \"{Path.Combine(cmpDir, "temp.jpg")}\" \"{imgB}\"");

            string siDiff = Path.Combine(cmpDir, "si_diff.png");
            string imDiff = Path.Combine(cmpDir, "im_diff.png");
            await RunSharpImage($"compare \"{imgA}\" \"{imgB}\" --diff \"{siDiff}\"");
            await RunMagick($"compare \"{imgA}\" \"{imgB}\" \"{imDiff}\"");
            RecordTransformResult("Compare", "diff image", siDiff, imDiff);
            File.Delete(Path.Combine(cmpDir, "temp.jpg"));
        }
        task.Increment(1);

        // Fourier
        {
            string fDir = Path.Combine(dir, "Fourier");
            string localSrc = Path.Combine(fDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(SourceSmallPng, localSrc, true);

            string siOut = Path.Combine(fDir, "si_magnitude.png");
            await RunSharpImage($"fourier \"{localSrc}\" \"{siOut}\"");
            RecordTransformResult("Fourier", "magnitude spectrum", siOut, "");
        }
        task.Increment(1);

        // Draw
        {
            string dDir = Path.Combine(dir, "Draw");
            string localSrc = Path.Combine(dDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut = Path.Combine(dDir, "si_text.png");
            string imOut = Path.Combine(dDir, "im_text.png");
            await RunSharpImage($"draw \"{localSrc}\" \"{siOut}\" --text \"Hello SharpImage\" -x 50 -y 50 --scale 3 --color red");
            await RunMagick($"\"{localSrc}\" -fill red -pointsize 36 -annotate +50+80 \"Hello SharpImage\" \"{imOut}\"");
            RecordTransformResult("Draw", "text overlay", siOut, imOut);
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 4b. ANALYSIS & DETECTION
    // ─────────────────────────────────────────────────────────────

    static async Task RunAnalysisOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Analysis");
        string src = SourcePng;

        // Canny edge detection
        {
            string aDir = Path.Combine(dir, "Canny");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut = Path.Combine(aDir, "si_canny.png");
            string imOut = Path.Combine(aDir, "im_canny.png");
            await RunSharpImage($"canny \"{localSrc}\" \"{siOut}\" --sigma 1.4 --low 0.1 --high 0.3");
            await RunMagick($"\"{localSrc}\" -canny 0x1.4+10%+30% \"{imOut}\"");
            RecordTransformResult("Canny", "edge detection", siOut, imOut);
        }
        task.Increment(1);

        // Connected components (on thresholded image)
        {
            string aDir = Path.Combine(dir, "ConnectedComponents");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            // First threshold to create binary image
            string threshSrc = Path.Combine(aDir, "thresholded.png");
            await RunSharpImage($"threshold \"{localSrc}\" \"{threshSrc}\" --value 0.5");

            string siOut = Path.Combine(aDir, "si_ccl.png");
            await RunSharpImage($"connectedcomponents \"{threshSrc}\" \"{siOut}\" --threshold 0.5");
            RecordTransformResult("ConnectedComponents", "object labeling", siOut, "");
        }
        task.Increment(1);

        // Mean-shift segmentation
        {
            string aDir = Path.Combine(dir, "MeanShift");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            // Use a smaller source for mean-shift (it's slow on large images)
            File.Copy(SourceSmallPng, localSrc, true);

            string siOut = Path.Combine(aDir, "si_meanshift.png");
            string imOut = Path.Combine(aDir, "im_meanshift.png");
            await RunSharpImage($"meanshift \"{localSrc}\" \"{siOut}\" --spatial 8 --color 0.15");
            await RunMagick($"\"{localSrc}\" -mean-shift 8x8+15% \"{imOut}\"");
            RecordTransformResult("MeanShift", "segmentation", siOut, imOut);
        }
        task.Increment(1);

        // Hough lines (on canny output)
        {
            string aDir = Path.Combine(dir, "HoughLines");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            // First do canny edge detection
            string edgeSrc = Path.Combine(aDir, "edges.png");
            await RunSharpImage($"canny \"{localSrc}\" \"{edgeSrc}\" --sigma 1.4 --low 0.1 --high 0.3");

            string siOut = Path.Combine(aDir, "si_houghlines.png");
            await RunSharpImage($"houghlines \"{edgeSrc}\" \"{siOut}\" --threshold 80 --max 15");

            // IM does hough differently (outputs MVG text), so just show our result
            RecordTransformResult("HoughLines", "line detection", siOut, "");
        }
        task.Increment(1);

        // Perceptual hash (text comparison, no image output)
        {
            string aDir = Path.Combine(dir, "PerceptualHash");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string hashFile = Path.Combine(aDir, "phash_result.txt");
            var phashResult = await RunSharpImage($"phash \"{localSrc}\"");
            File.WriteAllText(hashFile, phashResult.Output);
            RecordTransformResult("PerceptualHash", "fingerprint", hashFile, "");
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 5a. CHANNEL OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunChannelOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Channel");
        string src = SourcePng;

        // Separate channels
        {
            string aDir = Path.Combine(dir, "Separate");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siPrefix = Path.Combine(aDir, "si");
            await RunSharpImage($"separate \"{localSrc}\" \"{siPrefix}\"");

            // IM separate each channel individually
            string imRed = Path.Combine(aDir, "im_red.png");
            string imGreen = Path.Combine(aDir, "im_green.png");
            string imBlue = Path.Combine(aDir, "im_blue.png");
            await RunMagick($"\"{localSrc}\" -channel R -separate \"{imRed}\"");
            await RunMagick($"\"{localSrc}\" -channel G -separate \"{imGreen}\"");
            await RunMagick($"\"{localSrc}\" -channel B -separate \"{imBlue}\"");

            RecordTransformResult("SeparateChannels", "R/G/B split", siPrefix + "_red.png", imRed);
        }
        task.Increment(1);

        // Combine channels (round-trip: separate then combine)
        {
            string aDir = Path.Combine(dir, "Combine");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            // First separate
            string prefix = Path.Combine(aDir, "chan");
            await RunSharpImage($"separate \"{localSrc}\" \"{prefix}\"");

            // Then combine back
            string siOut = Path.Combine(aDir, "si_combined.png");
            await RunSharpImage($"combine \"{prefix}_red.png\" \"{prefix}_green.png\" \"{prefix}_blue.png\" \"{siOut}\"");
            RecordTransformResult("CombineChannels", "round-trip", siOut, localSrc);
        }
        task.Increment(1);

        // Swap channels (R <-> B)
        {
            string aDir = Path.Combine(dir, "Swap");
            string localSrc = Path.Combine(aDir, "source.png");
            EnsureDirectoryFor(localSrc);
            File.Copy(src, localSrc, true);

            string siOut = Path.Combine(aDir, "si_swap_rb.png");
            string imOut = Path.Combine(aDir, "im_swap_rb.png");
            await RunSharpImage($"swapchannel \"{localSrc}\" \"{siOut}\" --from red --to blue");
            await RunMagick($"\"{localSrc}\" -channel-fx \"blue,green,red\" \"{imOut}\"");
            RecordTransformResult("SwapChannels", "R<->B swap", siOut, imOut);
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 5b. METADATA OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunMetadataOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Metadata");

        // EXIF write + read round-trip
        {
            string localSrc = Path.Combine(dir, "ExifWrite");
            EnsureDirectoryFor(localSrc + Path.DirectorySeparatorChar);
            string src = Path.Combine(localSrc, "source.png");
            File.Copy(SourceSmallPng, src, true);

            string siOut = Path.Combine(localSrc, "si_exif_set.png");
            string siStripped = Path.Combine(localSrc, "si_exif_stripped.png");

            // Write EXIF tag
            await RunSharpImage($"exif \"{src}\" \"{siOut}\" --set Make=SharpImage --set Software=BigTest");

            // Read it back
            var readResult = await RunSharpImage($"exif \"{siOut}\"");
            bool hasExif = readResult.Output.Contains("SharpImage");
            results.Add(new TestResult
            {
                Category = "Metadata", Name = "EXIF Write+Read",
                SharpImagePassed = hasExif, ImageMagickPassed = true,
                SharpImageFile = siOut, Notes = hasExif ? "EXIF tags round-tripped" : "EXIF tags not found"
            });

            // Strip EXIF
            await RunSharpImage($"exif \"{siOut}\" \"{siStripped}\" --strip");
            var strippedResult = await RunSharpImage($"exif \"{siStripped}\"");
            bool stripped = strippedResult.Output.Contains("No EXIF");
            results.Add(new TestResult
            {
                Category = "Metadata", Name = "EXIF Strip",
                SharpImagePassed = stripped, ImageMagickPassed = true,
                SharpImageFile = siStripped, Notes = stripped ? "All EXIF removed" : "EXIF still present"
            });
        }
        task.Increment(1);

        // ICC Profile operations
        {
            string localSrc = Path.Combine(dir, "IccProfile");
            EnsureDirectoryFor(localSrc + Path.DirectorySeparatorChar);
            string src = Path.Combine(localSrc, "source.jpg");
            File.Copy(SourceMountains, src, true);

            string iccFile = Path.Combine(localSrc, "extracted.icc");

            // Read ICC info
            var readResult = await RunSharpImage($"iccprofile \"{src}\"");
            bool hasIcc = readResult.Output.Contains("ICC Profile");
            results.Add(new TestResult
            {
                Category = "Metadata", Name = "ICC Read",
                SharpImagePassed = hasIcc, ImageMagickPassed = true,
                SharpImageFile = src, Notes = hasIcc ? "ICC profile detected" : "No ICC profile found"
            });

            // Extract ICC
            await RunSharpImage($"iccprofile \"{src}\" --extract \"{iccFile}\"");
            bool extracted = File.Exists(iccFile) && new FileInfo(iccFile).Length > 100;
            results.Add(new TestResult
            {
                Category = "Metadata", Name = "ICC Extract",
                SharpImagePassed = extracted, ImageMagickPassed = true,
                SharpImageFile = iccFile, Notes = extracted ? $"ICC: {new FileInfo(iccFile).Length:N0} bytes" : "Extraction failed"
            });
        }
        task.Increment(1);

        // Strip all metadata
        {
            string localSrc = Path.Combine(dir, "StripAll");
            EnsureDirectoryFor(localSrc + Path.DirectorySeparatorChar);
            string src = Path.Combine(localSrc, "source.jpg");
            File.Copy(SourceMountains, src, true);

            string siOut = Path.Combine(localSrc, "si_stripped.png");

            await RunSharpImage($"metadata \"{src}\" \"{siOut}\" --strip-all");
            var checkResult = await RunSharpImage($"metadata \"{siOut}\"");
            bool allStripped = checkResult.Output.Contains("No metadata");
            results.Add(new TestResult
            {
                Category = "Metadata", Name = "Strip All Metadata",
                SharpImagePassed = allStripped, ImageMagickPassed = true,
                SharpImageFile = siOut, Notes = allStripped ? "All metadata removed" : "Some metadata remains"
            });
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 6. COLORSPACE ROUND-TRIPS
    // ─────────────────────────────────────────────────────────────

    static async Task RunColorspaceOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Colorspace");
        string src = SourceWizard;

        var colorspaces = new (string SiName, string ImName)[]
        {
            ("Oklab", "OkLab"),
            ("Oklch", "OkLCH"),
            ("JzAzBz", "Jzazbz"),
            ("JzCzhz", "Jzazbz"),   // IM lacks separate JzCzhz; uses Jzazbz
            ("DisplayP3", "DisplayP3"),
            ("ProPhoto", "ProPhoto"),
        };

        foreach (var (siCs, imCs) in colorspaces)
        {
            string csDir = Path.Combine(dir, siCs);
            EnsureDirectoryFor(Path.Combine(csDir, "_"));

            string siOut = Path.Combine(csDir, "si_roundtrip.png");
            string imOut = Path.Combine(csDir, "im_roundtrip.png");

            // SharpImage round-trip
            var siResult = await RunSharpImage($"colorspace-roundtrip \"{src}\" \"{siOut}\" --via {siCs}");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            // ImageMagick round-trip (convert to cs then back to sRGB)
            var imResult = await RunMagick($"\"{src}\" -colorspace {imCs} -colorspace sRGB \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Colorspace", Name = $"{siCs} Round-Trip",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = siPassed && imPassed ? "Both round-tripped successfully" : "Check outputs"
            });
            task.Increment(1);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 7. NOVEL FEATURES
    // ─────────────────────────────────────────────────────────────

    static async Task RunNovelFeatures(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "SeamCarving");
        EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
        EnsureDirectoryFor(Path.Combine(dir, "im", "_"));
        string src = SourceWizard;

        // Seam carve — 75% width
        {
            string siOut = Path.Combine(dir, "si", "seamcarve_75.png");
            string imOut = Path.Combine(dir, "im", "seamcarve_75.png");

            var siResult = await RunSharpImage($"seamcarve \"{src}\" \"{siOut}\" -w 828 -h 1468");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -liquid-rescale 828x1468! \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Novel", Name = "Seam Carve 75%",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Content-aware resize to 75% width"
            });
        }
        task.Increment(1);

        // Energy map visualization
        {
            string siOut = Path.Combine(dir, "si", "energy.png");

            var siResult = await RunSharpImage($"energymap \"{src}\" \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Novel", Name = "Energy Map",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut,
                Notes = "Seam carving energy visualization (SI-only feature)"
            });
        }
        task.Increment(1);

        // Histogram matching — wizard matched to rose's colors
        {
            string hmDir = Path.Combine(BigTestDir, "HistogramMatch");
            EnsureDirectoryFor(Path.Combine(hmDir, "si", "_"));
            EnsureDirectoryFor(Path.Combine(hmDir, "im", "_"));

            string siOut = Path.Combine(hmDir, "si", "source_matched_to_reference.png");
            string siHist = Path.Combine(hmDir, "si", "wizard_histogram.png");
            string imHist = Path.Combine(hmDir, "im", "wizard_histogram.png");

            var siResult = await RunSharpImage($"histmatch \"{src}\" \"{SourceSmallPng}\" -o \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Novel", Name = "Histogram Match",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut,
                Notes = "Transfer reference colors to source (SI-only feature)"
            });
            task.Increment(1);

            // SI histogram visualization
            var siHistResult = await RunSharpImage($"histogram \"{src}\" \"{siHist}\"");
            bool siHistPassed = siHistResult.ExitCode == 0 && File.Exists(siHist);

            // IM histogram visualization
            var imHistResult = await RunMagick($"\"{src}\" -define histogram:unique-colors=false histogram:\"{imHist}\"");
            bool imHistPassed = imHistResult.ExitCode == 0 && File.Exists(imHist);

            results.Add(new TestResult
            {
                Category = "Novel", Name = "Histogram Visualization",
                SharpImagePassed = siHistPassed, ImageMagickPassed = imHistPassed,
                SharpImageFile = siHist, ImageMagickFile = imHist,
                Notes = "RGB histogram comparison"
            });
            task.Increment(1);
        }

        // Smart Crop
        {
            string siDir = Path.Combine(BigTestDir, "SmartCrop", "si");
            string imDir = Path.Combine(BigTestDir, "SmartCrop", "im");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));
            EnsureDirectoryFor(Path.Combine(imDir, "x"));

            string siSmartCrop = Path.Combine(siDir, "smartcrop_400x400.png");
            string imCenterCrop = Path.Combine(imDir, "centercrop_400x400.png");
            string siInterestMap = Path.Combine(siDir, "interestmap.png");

            var siSmartResult = await RunSharpImage($"smartcrop \"{src}\" \"{siSmartCrop}\" -w 400 -h 400");
            bool siSmartPassed = siSmartResult.ExitCode == 0 && File.Exists(siSmartCrop);
            var imCenterResult = await RunMagick($"\"{src}\" -gravity center -crop 400x400+0+0 +repage \"{imCenterCrop}\"");
            bool imCenterPassed = imCenterResult.ExitCode == 0 && File.Exists(imCenterCrop);
            var siMapResult = await RunSharpImage($"interestmap \"{src}\" \"{siInterestMap}\"");
            bool siMapPassed = siMapResult.ExitCode == 0 && File.Exists(siInterestMap);

            results.Add(new TestResult {
                Category = "Novel", Name = "Smart Crop 400×400",
                SharpImagePassed = siSmartPassed, ImageMagickPassed = imCenterPassed,
                SharpImageFile = siSmartCrop, ImageMagickFile = imCenterCrop,
                Notes = "Entropy-based vs center crop"
            });
            results.Add(new TestResult {
                Category = "Novel", Name = "Interest Map",
                SharpImagePassed = siMapPassed, ImageMagickPassed = true,
                SharpImageFile = siInterestMap,
                Notes = "Saliency heatmap (SI-only)"
            });
            task.Increment(2);
        }

        // Pipeline
        {
            string siDir = Path.Combine(BigTestDir, "Pipeline", "si");
            string imDir = Path.Combine(BigTestDir, "Pipeline", "im");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));
            EnsureDirectoryFor(Path.Combine(imDir, "x"));

            string siResult = Path.Combine(siDir, "pipeline_result.png");
            string imResult = Path.Combine(imDir, "pipeline_result.png");

            var siPipeResult = await RunSharpImage($"pipeline \"{src}\" \"{siResult}\" -s \"resize 400 500\" -s \"blur 1.5\" -s \"sepia\" -s \"vignette 15\"");
            bool siPassed = siPipeResult.ExitCode == 0 && File.Exists(siResult);
            var imPipeResult = await RunMagick($"\"{src}\" -resize 400x500! -blur 0x1.5 -sepia-tone 80% -vignette 0x15 \"{imResult}\"");
            bool imPassed = imPipeResult.ExitCode == 0 && File.Exists(imResult);

            results.Add(new TestResult {
                Category = "Novel", Name = "Pipeline (4-step chain)",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siResult, ImageMagickFile = imResult,
                Notes = "Resize→Blur→Sepia→Vignette"
            });
            task.Increment(1);
        }

        // Tensor Export
        {
            string siDir = Path.Combine(BigTestDir, "TensorExport", "si");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));

            string siTensor = Path.Combine(siDir, "wizard_chw.sitf");
            string siTensorHwc = Path.Combine(siDir, "wizard_hwc_imagenet.sitf");

            var siResult1 = await RunSharpImage($"tensor \"{src}\" \"{siTensor}\" --stats");
            bool siPassed1 = siResult1.ExitCode == 0 && File.Exists(siTensor);
            var siResult2 = await RunSharpImage($"tensor \"{src}\" \"{siTensorHwc}\" --layout hwc --normalize imagenet");
            bool siPassed2 = siResult2.ExitCode == 0 && File.Exists(siTensorHwc);

            results.Add(new TestResult {
                Category = "Novel", Name = "Tensor Export (CHW)",
                SharpImagePassed = siPassed1, ImageMagickPassed = true,
                SharpImageFile = siTensor,
                Notes = "CHW float32 tensor (SI-only, ML/AI interop)"
            });
            results.Add(new TestResult {
                Category = "Novel", Name = "Tensor Export (HWC+ImageNet)",
                SharpImagePassed = siPassed2, ImageMagickPassed = true,
                SharpImageFile = siTensorHwc,
                Notes = "HWC ImageNet-normalized tensor (SI-only)"
            });
            task.Increment(2);
        }

        // Palette Extraction
        {
            string siDir = Path.Combine(BigTestDir, "Palette", "si");
            string imDir = Path.Combine(BigTestDir, "Palette", "im");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));
            EnsureDirectoryFor(Path.Combine(imDir, "x"));

            string siSwatch = Path.Combine(siDir, "swatch.png");
            string siMapped = Path.Combine(siDir, "wizard_8colors.png");
            string imMapped = Path.Combine(imDir, "wizard_8colors.png");

            var siSwatchResult = await RunSharpImage($"palette \"{src}\" \"{siSwatch}\" -n 8 --map \"{siMapped}\"");
            bool siPassed = siSwatchResult.ExitCode == 0 && File.Exists(siSwatch) && File.Exists(siMapped);
            var imResult = await RunMagick($"\"{src}\" -colors 8 -dither None \"{imMapped}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imMapped);

            results.Add(new TestResult {
                Category = "Novel", Name = "Palette Swatch",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siSwatch,
                Notes = "k-means++ palette swatch (SI-only)"
            });
            results.Add(new TestResult {
                Category = "Novel", Name = "Palette Map (8 colors)",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siMapped, ImageMagickFile = imMapped,
                Notes = "8-color quantization comparison"
            });
            task.Increment(2);
        }

        // Perceptual Diff
        {
            string siDir = Path.Combine(BigTestDir, "PerceptualDiff", "si");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));

            string rosePng = Path.Combine(ImagesDir, "photo_small.png");
            string roseJpg = Path.Combine(ImagesDir, "photo_small.jpg");
            string siSsim = Path.Combine(siDir, "ssim_map.png");
            string siDeltaE = Path.Combine(siDir, "deltae_map.png");

            var siResult = await RunSharpImage($"diff \"{rosePng}\" \"{roseJpg}\" --ssim \"{siSsim}\" --deltae \"{siDeltaE}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siSsim) && File.Exists(siDeltaE);

            results.Add(new TestResult {
                Category = "Novel", Name = "Perceptual Diff (SSIM map)",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siSsim,
                Notes = "SSIM heatmap PNG vs JPG (SI-only)"
            });
            results.Add(new TestResult {
                Category = "Novel", Name = "Perceptual Diff (Delta-E map)",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siDeltaE,
                Notes = "CIE Delta-E 2000 heatmap (SI-only)"
            });
            task.Increment(2);
        }

        // Animated WebP
        {
            string siDir = Path.Combine(BigTestDir, "AnimWebp", "si");
            string imDir = Path.Combine(BigTestDir, "AnimWebp", "im");
            EnsureDirectoryFor(Path.Combine(siDir, "x"));
            EnsureDirectoryFor(Path.Combine(imDir, "x"));

            string wizard = Path.Combine(ImagesDir, "peppers_rgba.png");
            string rose = Path.Combine(ImagesDir, "photo_small.png");
            string imAnimGif = Path.Combine(imDir, "anim_3frame.gif");
            string siAnimWebp = Path.Combine(siDir, "anim_3frame.webp");

            // IM: create animated GIF
            var imResult = await RunMagick($"\"{wizard}\" -resize 200x200 \"{rose}\" -resize 200x200 -delay 50 -loop 0 \"{imAnimGif}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imAnimGif);

            // SI: convert the animated GIF to animated WebP
            var siResult = await RunSharpImage($"animwebp \"{imAnimGif}\" \"{siAnimWebp}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siAnimWebp) && new FileInfo(siAnimWebp).Length > 100;

            results.Add(new TestResult {
                Category = "Novel", Name = "Animated WebP",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siAnimWebp, ImageMagickFile = imAnimGif,
                Notes = "GIF→WebP animated conversion (SI) vs animated GIF (IM)"
            });
            task.Increment(2);
        }

        // Modern Dithering (blue noise, stucki, atkinson, sierra)
        {
            string ditherDir = Path.Combine(BigTestDir, "Dithering");
            string siDir = Path.Combine(ditherDir, "si");
            string imDir = Path.Combine(ditherDir, "im");
            EnsureDirectoryFor(Path.Combine(siDir, "_"));
            EnsureDirectoryFor(Path.Combine(imDir, "_"));

            string[] methods = ["bluenoise", "stucki", "atkinson", "sierra"];
            foreach (string method in methods)
            {
                string siOut = Path.Combine(siDir, $"{method}.png");
                string imOut = Path.Combine(imDir, $"{method}_im.png");

                var siResult = await RunSharpImage($"moderndither \"{SourceWizard}\" \"{siOut}\" -m {method}");
                bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

                var imResult = await RunMagick($"\"{SourceWizard}\" -dither FloydSteinberg -monochrome \"{imOut}\"");
                bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

                results.Add(new TestResult {
                    Category = "Novel", Name = $"Dither ({method})",
                    SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                    SharpImageFile = siOut, ImageMagickFile = imOut,
                    Notes = $"{method} dithering (SI) vs Floyd-Steinberg (IM)"
                });
            }
            task.Increment(2);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 8. MATH & VISUAL OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunMathVisualOperations(ProgressTask task)
    {
        // Evaluate: Multiply brightness
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
            EnsureDirectoryFor(Path.Combine(dir, "im", "_"));
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "eval_multiply.png");
            string imOut = Path.Combine(dir, "im", "eval_multiply.png");

            var siResult = await RunSharpImage($"evaluate \"{src}\" \"{siOut}\" -op multiply -v 1.5");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -evaluate Multiply 1.5 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Evaluate Multiply 1.5x",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Per-pixel multiply by 1.5"
            });
        }
        task.Increment(1);

        // Evaluate: Threshold
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "eval_threshold.png");
            string imOut = Path.Combine(dir, "im", "eval_threshold.png");

            var siResult = await RunSharpImage($"evaluate \"{src}\" \"{siOut}\" -op threshold -v 32768");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -evaluate Threshold 50% \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Evaluate Threshold 50%",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Per-pixel binary threshold"
            });
        }
        task.Increment(1);

        // Statistic: Median filter
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "stat_median.png");
            string imOut = Path.Combine(dir, "im", "stat_median.png");

            var siResult = await RunSharpImage($"statistic \"{src}\" \"{siOut}\" -t median -r 2");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -statistic Median 5x5 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Statistic Median r=2",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Median filter 5x5 neighborhood"
            });
        }
        task.Increment(1);

        // Statistic: Gradient (edge detection)
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "stat_gradient.png");
            string imOut = Path.Combine(dir, "im", "stat_gradient.png");

            var siResult = await RunSharpImage($"statistic \"{src}\" \"{siOut}\" -t gradient -r 1");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -statistic Gradient 3x3 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Statistic Gradient r=1",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Gradient-based edge emphasis"
            });
        }
        task.Increment(1);

        // Function: Sinusoid
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "func_sinusoid.png");

            var siResult = await RunSharpImage($"function \"{src}\" \"{siOut}\" -f sinusoid -p 0.5,3,0,0.5");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Function Sinusoid",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Sinusoidal tone mapping"
            });
        }
        task.Increment(1);

        // Enhance (noise reduction)
        {
            string dir = Path.Combine(BigTestDir, "Effects");
            EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
            EnsureDirectoryFor(Path.Combine(dir, "im", "_"));
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "enhance.png");
            string imOut = Path.Combine(dir, "im", "enhance.png");

            var siResult = await RunSharpImage($"enhance \"{src}\" \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -enhance \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "Enhance (Noise Reduction)",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Edge-preserving noise reduction"
            });
        }
        task.Increment(1);

        // CDL color grading
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "cdl_warm.png");

            var siResult = await RunSharpImage($"cdl \"{src}\" --slope 1.1,1.0,0.9 --offset 0.02,0,0 --power 0.9,1.0,1.1 --saturation 1.2 \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "CDL Warm Grade",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "ASC-CDL warm color grading"
            });
        }
        task.Increment(1);

        // ChromaKey
        {
            string dir = Path.Combine(BigTestDir, "Advanced");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "chromakey.png");

            var siResult = await RunSharpImage($"chromakey \"{src}\" \"{siOut}\" -c 00FF00 -t 0.2");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "ChromaKey Green",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Green screen removal"
            });
        }
        task.Increment(1);

        // ChannelFx: swap red/blue
        {
            string dir = Path.Combine(BigTestDir, "Channel");
            EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "channelfx_swap_rb.png");
            string imOut = Path.Combine(dir, "im", "channelfx_swap_rb.png");

            var siResult = await RunSharpImage($"channelfx \"{src}\" \"{siOut}\" -e \"swap:red,blue\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -channel-fx \"| swap\" \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "ChannelFx Swap R/B",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Swap red and blue channels"
            });
        }
        task.Increment(1);

        // UnsharpMask
        {
            string dir = Path.Combine(BigTestDir, "Effects");
            string src = SourceWizard;

            string siOut = Path.Combine(dir, "si", "unsharpmask.png");
            string imOut = Path.Combine(dir, "im", "unsharpmask.png");

            var siResult = await RunSharpImage($"unsharpmask \"{src}\" \"{siOut}\" -s 2.0 -a 1.5 -t 0");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -unsharp 2x1.0+1.5+0 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Math", Name = "UnsharpMask",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Unsharp mask sharpening"
            });
        }
        task.Increment(1);
    }

    static async Task RunUtilityOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Utility");
        EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
        EnsureDirectoryFor(Path.Combine(dir, "im", "_"));
        string src = SourceWizard;

        // Thumbnail
        {
            string siOut = Path.Combine(dir, "si", "thumbnail_150.png");
            string imOut = Path.Combine(dir, "im", "thumbnail_150.png");

            var siResult = await RunSharpImage($"thumbnail \"{src}\" \"{siOut}\" --width 150 --height 150");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -thumbnail 150x150 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "Thumbnail 150×150",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Aspect-preserving thumbnail"
            });
        }
        task.Increment(1);

        // Resample
        {
            string siOut = Path.Combine(dir, "si", "resample_36dpi.png");
            string imOut = Path.Combine(dir, "im", "resample_36dpi.png");

            var siResult = await RunSharpImage($"resample \"{src}\" \"{siOut}\" --dpi 36");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -resample 36 \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "Resample to 36 DPI",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "DPI-proportional resize"
            });
        }
        task.Increment(1);

        // Magnify (2×)
        {
            string smallSrc = SourceSmallPng;
            string siOut = Path.Combine(dir, "si", "magnify_2x.png");
            string imOut = Path.Combine(dir, "im", "magnify_2x.png");

            var siResult = await RunSharpImage($"magnify \"{smallSrc}\" \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{smallSrc}\" -magnify \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "Magnify 2×",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Double dimensions"
            });
        }
        task.Increment(1);

        // Minify (½×)
        {
            string siOut = Path.Combine(dir, "si", "minify_half.png");
            string imOut = Path.Combine(dir, "im", "minify_half.png");

            var siResult = await RunSharpImage($"minify \"{src}\" \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            var imResult = await RunMagick($"\"{src}\" -minify \"{imOut}\"");
            bool imPassed = imResult.ExitCode == 0 && File.Exists(imOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "Minify ½×",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Halve dimensions"
            });
        }
        task.Increment(1);

        // CropToTiles
        {
            string siTileDir = Path.Combine(dir, "si", "tiles");
            string imTileDir = Path.Combine(dir, "im", "tiles");
            Directory.CreateDirectory(siTileDir);
            Directory.CreateDirectory(imTileDir);

            var siResult = await RunSharpImage($"croptiles \"{src}\" \"{siTileDir}\" --tile-width 128 --tile-height 128");
            bool siPassed = siResult.ExitCode == 0 && Directory.Exists(siTileDir)
                && Directory.GetFiles(siTileDir, "*.png").Length > 0;

            var imResult = await RunMagick($"\"{src}\" -crop 128x128 \"{Path.Combine(imTileDir, "tile_%d.png")}\"");
            bool imPassed = imResult.ExitCode == 0;

            results.Add(new TestResult
            {
                Category = "Utility", Name = "CropToTiles 128×128",
                SharpImagePassed = siPassed, ImageMagickPassed = imPassed,
                SharpImageFile = siTileDir, ImageMagickFile = imTileDir,
                Notes = "Split into 128px grid tiles"
            });
        }
        task.Increment(1);

        // TextureImage (tile pattern)
        {
            string siOut = Path.Combine(dir, "si", "texture_tiled.png");

            var siResult = await RunSharpImage($"textureimage \"{src}\" \"{SourceSmallPng}\" \"{siOut}\"");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "TextureImage Tile",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Tile texture across target"
            });
        }
        task.Increment(1);

        // RangeThreshold
        {
            string siOut = Path.Combine(dir, "si", "rangethreshold.png");

            var siResult = await RunSharpImage($"rangethreshold \"{src}\" \"{siOut}\" --hard-low 0.1 --soft-low 0.3 --soft-high 0.7 --hard-high 0.9");
            bool siPassed = siResult.ExitCode == 0 && File.Exists(siOut);

            results.Add(new TestResult
            {
                Category = "Utility", Name = "Range Threshold",
                SharpImagePassed = siPassed, ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Multi-level soft threshold"
            });
        }
        task.Increment(1);
    }

    // ─────────────────────────────────────────────────────────────
    // 10. ALPHA & VIRTUAL PIXEL OPERATIONS
    // ─────────────────────────────────────────────────────────────

    static async Task RunAlphaPixelOperations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "AlphaPixel");
        string src = SourceWizard;       // RGBA source (peppers_rgba.png)
        string srcRgb = SourceMountains; // RGB (no alpha)

        EnsureDirectoryFor(Path.Combine(dir, "si", "_"));
        EnsureDirectoryFor(Path.Combine(dir, "im", "_"));

        // 1. Alpha Extract
        {
            string siOut = Path.Combine(dir, "si", "alpha_extract.png");
            string imOut = Path.Combine(dir, "im", "alpha_extract.png");
            var siResult = await RunSharpImage($"alpha-extract \"{src}\" \"{siOut}\"");
            var imResult = await RunMagick($"\"{src}\" -alpha extract \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Alpha Extract",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Extract alpha channel as grayscale"
            });
        }
        task.Increment(1);

        // 2. Alpha Remove (composite over white)
        {
            string siOut = Path.Combine(dir, "si", "alpha_remove.png");
            string imOut = Path.Combine(dir, "im", "alpha_remove.png");
            var siResult = await RunSharpImage($"alpha-remove \"{src}\" \"{siOut}\"");
            var imResult = await RunMagick($"\"{src}\" -background white -alpha remove \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Alpha Remove",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Flatten alpha over white background"
            });
        }
        task.Increment(1);

        // 3. Alpha Set (semi-transparent)
        {
            string siOut = Path.Combine(dir, "si", "alpha_set_half.png");
            string imOut = Path.Combine(dir, "im", "alpha_set_half.png");
            var siResult = await RunSharpImage($"alpha-set \"{srcRgb}\" \"{siOut}\" --value 32768");
            var imResult = await RunMagick($"\"{srcRgb}\" -alpha set -channel A -evaluate set 50% +channel \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Alpha Set 50%",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Set 50% alpha on opaque image"
            });
        }
        task.Increment(1);

        // 4. Alpha Opaque
        {
            string siOut = Path.Combine(dir, "si", "alpha_opaque.png");
            string imOut = Path.Combine(dir, "im", "alpha_opaque.png");
            var siResult = await RunSharpImage($"alpha-opaque \"{src}\" \"{siOut}\"");
            var imResult = await RunMagick($"\"{src}\" -alpha opaque \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Alpha Opaque",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Force all pixels fully opaque"
            });
        }
        task.Increment(1);

        // 5. Alpha Transparent (make white transparent)
        {
            string siOut = Path.Combine(dir, "si", "alpha_transparent.png");
            var siResult = await RunSharpImage($"alpha-transparent \"{srcRgb}\" \"{siOut}\" --color FFFFFF --fuzz 0.2");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Alpha Transparent",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Make white pixels transparent (fuzz=0.2)"
            });
        }
        task.Increment(1);

        // 6. Sample (point-sample resize)
        {
            string siOut = Path.Combine(dir, "si", "sample_200x150.png");
            string imOut = Path.Combine(dir, "im", "sample_200x150.png");
            var siResult = await RunSharpImage($"sample \"{srcRgb}\" \"{siOut}\" --width 200 --height 150");
            var imResult = await RunMagick($"\"{srcRgb}\" -sample 200x150! \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Sample 200×150",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Point-sample resize to 200×150"
            });
        }
        task.Increment(1);

        // 7. Adaptive Resize
        {
            string siOut = Path.Combine(dir, "si", "adaptive_200x150.png");
            string imOut = Path.Combine(dir, "im", "adaptive_200x150.png");
            var siResult = await RunSharpImage($"adaptive-resize \"{srcRgb}\" \"{siOut}\" --width 200 --height 150");
            var imResult = await RunMagick($"\"{srcRgb}\" -adaptive-resize 200x150! \"{imOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Adaptive Resize 200×150",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = imResult.ExitCode == 0 && File.Exists(imOut),
                SharpImageFile = siOut, ImageMagickFile = imOut,
                Notes = "Adaptive resize to 200×150"
            });
        }
        task.Increment(1);

        // 8. Distance Transform
        {
            string binaryInput = Path.Combine(dir, "si", "dt_binary_input.png");
            string siOut = Path.Combine(dir, "si", "distance_transform.png");
            await RunSharpImage($"threshold \"{srcRgb}\" \"{binaryInput}\" --value 0.5");
            var siResult = await RunSharpImage($"distance-transform \"{binaryInput}\" \"{siOut}\"");
            results.Add(new TestResult
            {
                Category = "AlphaPixel", Name = "Distance Transform",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Euclidean distance transform on thresholded image"
            });
        }
        task.Increment(1);
    }

    // 11. PIXEL & THRESHOLD OPERATIONS (Phase 49)
    static async Task RunPhase49Operations(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "PixelThreshold");
        EnsureDirectoryFor(Path.Combine(dir, "dummy"));

        // 1. Sort Pixels
        {
            string siOut = Path.Combine(dir, "sort_pixels.png");
            string imOut = Path.Combine(dir, "sort_pixels_im.png");
            var siResult = await RunSharpImage($"sortpixels \"{SourceWizard}\" \"{siOut}\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Sort Pixels",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Pixels sorted by luminance per row"
            });
        }
        task.Increment(1);

        // 2. Color Threshold
        {
            string siOut = Path.Combine(dir, "color_threshold.png");
            var siResult = await RunSharpImage($"colorthreshold \"{SourceWizard}\" \"{siOut}\" --start \"0.2,0.0,0.0\" --end \"1.0,0.5,0.5\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Color Threshold",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Threshold by RGB color range"
            });
        }
        task.Increment(1);

        // 3. Random Threshold
        {
            string siOut = Path.Combine(dir, "random_threshold.png");
            var siResult = await RunSharpImage($"randomthreshold \"{SourceWizard}\" \"{siOut}\" --low 0.3 --high 0.7");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Random Threshold",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Stochastic dithered threshold"
            });
        }
        task.Increment(1);

        // 4. Unique Colors palette
        {
            string siOut = Path.Combine(dir, "unique_colors.png");
            var siResult = await RunSharpImage($"uniquecolors \"{SourceSmallPng}\" \"{siOut}\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Unique Colors Palette",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "1-pixel-tall strip of all unique colors"
            });
        }
        task.Increment(1);

        // 5. Clamp
        {
            string siOut = Path.Combine(dir, "clamped.png");
            var siResult = await RunSharpImage($"clamp \"{SourceWizard}\" \"{siOut}\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Clamp",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Force pixel values into valid range"
            });
        }
        task.Increment(1);

        // 6. Cycle Colormap
        {
            string siOut = Path.Combine(dir, "cycle_colormap.png");
            var siResult = await RunSharpImage($"cycle \"{SourceWizard}\" \"{siOut}\" --shift 0.33");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Cycle Colormap",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Color values rotated by 33%"
            });
        }
        task.Increment(1);

        // 7. Mode Filter
        {
            string siOut = Path.Combine(dir, "mode_filter.png");
            var siResult = await RunSharpImage($"modefilter \"{SourceSmallPng}\" \"{siOut}\" -r 2");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Mode Filter",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Mode filter r=2 for noise removal"
            });
        }
        task.Increment(1);

        // 8. Level Colors
        {
            string siOut = Path.Combine(dir, "level_colors.png");
            var siResult = await RunSharpImage($"levelcolors \"{SourceWizard}\" \"{siOut}\" --black \"0.1,0.0,0.2\" --white \"1.0,0.9,0.7\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Level Colors",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "Map black→purple tint, white→warm tint"
            });
        }
        task.Increment(1);

        // 9. Strip Metadata
        {
            string siOut = Path.Combine(dir, "stripped.png");
            var siResult = await RunSharpImage($"strip \"{SourceMountains}\" \"{siOut}\"");
            results.Add(new TestResult
            {
                Category = "PixelThreshold", Name = "Strip Metadata",
                SharpImagePassed = siResult.ExitCode == 0 && File.Exists(siOut),
                ImageMagickPassed = true,
                SharpImageFile = siOut, ImageMagickFile = "",
                Notes = "All metadata removed, pixels identical"
            });
        }
        task.Increment(1);
    }

    static async Task RunPerformanceComparison(ProgressTask task)
    {
        string dir = Path.Combine(BigTestDir, "Performance");
        string src = SourceMountains; // large JPEG

        // Create a PNG version for non-JPEG inputs
        string srcPng = Path.Combine(dir, "source.png");
        EnsureDirectoryFor(srcPng);
        File.Copy(src, Path.Combine(dir, "source.jpg"), true);
        await RunSharpImage($"convert \"{src}\" \"{srcPng}\"");

        const int iterations = 5;

        var benchmarks = new (string Name, string SiArgs, string ImArgs)[]
        {
            ("JPEG→PNG", $"convert \"{src}\" \"{Path.Combine(dir, "si_jpg2png.png")}\"",
                         $"\"{src}\" \"{Path.Combine(dir, "im_jpg2png.png")}\""),

            ("PNG→WebP", $"convert \"{srcPng}\" \"{Path.Combine(dir, "si_png2webp.webp")}\"",
                         $"\"{srcPng}\" \"{Path.Combine(dir, "im_png2webp.webp")}\""),

            ("Resize 50%", $"resize \"{srcPng}\" \"{Path.Combine(dir, "si_resize50.png")}\" -w 750 -h 500 -m lanczos",
                           $"\"{srcPng}\" -filter Lanczos -resize 750x500! \"{Path.Combine(dir, "im_resize50.png")}\""),

            ("Blur σ=2", $"blur \"{srcPng}\" \"{Path.Combine(dir, "si_blur.png")}\" -s 2.0",
                         $"\"{srcPng}\" -gaussian-blur 0x2 \"{Path.Combine(dir, "im_blur.png")}\""),

            ("Grayscale", $"grayscale \"{srcPng}\" \"{Path.Combine(dir, "si_gray.png")}\"",
                          $"\"{srcPng}\" -colorspace Gray \"{Path.Combine(dir, "im_gray.png")}\""),

            ("Edge", $"edge \"{srcPng}\" \"{Path.Combine(dir, "si_edge.png")}\"",
                     $"\"{srcPng}\" -edge 1 \"{Path.Combine(dir, "im_edge.png")}\""),

            ("Sharpen", $"sharpen \"{srcPng}\" \"{Path.Combine(dir, "si_sharp.png")}\" -s 1.0 -a 1.5",
                        $"\"{srcPng}\" -sharpen 0x1 \"{Path.Combine(dir, "im_sharp.png")}\""),

            ("Rotate 90°", $"rotate \"{srcPng}\" \"{Path.Combine(dir, "si_rot90.png")}\" -a 90",
                           $"\"{srcPng}\" -rotate 90 \"{Path.Combine(dir, "im_rot90.png")}\""),

            // Phase 15 operations
            ("Equalize", $"equalize \"{srcPng}\" \"{Path.Combine(dir, "si_equalize.png")}\"",
                         $"\"{srcPng}\" -equalize \"{Path.Combine(dir, "im_equalize.png")}\""),

            ("Normalize", $"normalize \"{srcPng}\" \"{Path.Combine(dir, "si_normalize.png")}\"",
                          $"\"{srcPng}\" -normalize \"{Path.Combine(dir, "im_normalize.png")}\""),

            ("MotionBlur", $"motionblur \"{srcPng}\" \"{Path.Combine(dir, "si_motionblur.png")}\" -r 5 -a 45",
                           $"\"{srcPng}\" -motion-blur 0x5+45 \"{Path.Combine(dir, "im_motionblur.png")}\""),

            ("OilPaint", $"oilpaint \"{srcPng}\" \"{Path.Combine(dir, "si_oilpaint.png")}\" -r 3",
                         $"\"{srcPng}\" -paint 3 \"{Path.Combine(dir, "im_oilpaint.png")}\""),

            ("Swirl", $"swirl \"{srcPng}\" \"{Path.Combine(dir, "si_swirl.png")}\" -d 90",
                      $"\"{srcPng}\" -swirl 90 \"{Path.Combine(dir, "im_swirl.png")}\""),

            ("Composite Over", $"composite \"{srcPng}\" \"{SourcePng}\" \"{Path.Combine(dir, "si_comp_over.png")}\" -m over",
                               $"\"{srcPng}\" \"{SourcePng}\" -composite \"{Path.Combine(dir, "im_comp_over.png")}\""),

            ("Canny Edge", $"canny \"{srcPng}\" \"{Path.Combine(dir, "si_canny.png")}\"",
                           $"\"{srcPng}\" -canny 0x1+10%+30% \"{Path.Combine(dir, "im_canny.png")}\""),

            ("Charcoal", $"charcoal \"{srcPng}\" \"{Path.Combine(dir, "si_charcoal.png")}\"",
                         $"\"{srcPng}\" -charcoal 1 \"{Path.Combine(dir, "im_charcoal.png")}\""),
        };

        foreach (var (name, siArgs, imArgs) in benchmarks)
        {
            // Warmup both tools
            await RunSharpImage(siArgs, captureMemory: false);
            await RunProcess(MagickExe, imArgs, 60, false);

            var siTimes = new long[iterations];
            var imTimes = new long[iterations];
            double siPeakMB = 0, imPeakMB = 0;

            for (int i = 0;i < iterations;i++)
            {
                var siSw = Stopwatch.StartNew();
                var siProc = await RunSharpImage(siArgs, captureMemory: i == 0);
                siSw.Stop();
                siTimes[i] = siSw.ElapsedMilliseconds;
                if (i == 0)
                {
                    siPeakMB = siProc.PeakMemoryMB;
                }

                var imSw = Stopwatch.StartNew();
                var imProc = await RunProcess(MagickExe, imArgs, 60, captureMemory: i == 0);
                imSw.Stop();
                imTimes[i] = imSw.ElapsedMilliseconds;
                if (i == 0)
                {
                    imPeakMB = imProc.PeakMemoryMB;
                }
            }

            Array.Sort(siTimes);
            Array.Sort(imTimes);
            long siMedian = siTimes[iterations / 2];
            long imMedian = imTimes[iterations / 2];

            perfResults.Add(new PerfResult
            {
                Operation = name,
                SharpImageMs = siMedian,
                ImageMagickMs = imMedian,
                SharpImagePeakMB = siPeakMB,
                ImageMagickPeakMB = imPeakMB,
            });

            task.Increment(1);
        }
    }

    /// <summary>
    /// Runs SharpImage CLI as a direct exe (no dotnet run overhead).
    /// </summary>
    // ─────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────

    static void RecordTransformResult(string category, string name, string siFile, string imFile)
    {
        bool siOk = File.Exists(siFile) && new FileInfo(siFile).Length > 0;
        bool imOk = string.IsNullOrEmpty(imFile) || (File.Exists(imFile) && new FileInfo(imFile).Length > 0);

        results.Add(new TestResult
        {
            Category = category,
            Name = name,
            SharpImagePassed = siOk,
            ImageMagickPassed = imOk,
            SharpImageFile = siFile,
            ImageMagickFile = imFile,
        });
    }

    static async Task<ProcessResult> RunSharpImage(string args, bool captureMemory = false)
    {
        return await RunProcess(SharpImageExe, args, 60, captureMemory);
    }

    static async Task<ProcessResult> RunMagick(string args, bool captureMemory = false)
    {
        return await RunProcess(MagickExe, args, 60, captureMemory);
    }

    static async Task<(int ExitCode, string Output, string Error)> RunProcess(string exe, string args, int timeoutSec)
    {
        var r = await RunProcess(exe, args, timeoutSec, false);
        return (r.ExitCode, r.Output, r.Error);
    }

    static async Task<ProcessResult> RunProcess(string exe, string args, int timeoutSec, bool captureMemory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        double peakMB = 0;

        if (captureMemory)
        {
            // Poll peak memory while process runs
            _ = Task.Run(() =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        proc.Refresh();
                        double mb = proc.PeakWorkingSet64 / (1024.0 * 1024.0);
                        if (mb > peakMB)
                        {
                            peakMB = mb;
                        }

                        Thread.Sleep(10);
                    }
                }
                catch
                { /* process exited */
                }
            });
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(true);
            }
            catch
            {
            }
            return new ProcessResult { ExitCode = -1, Output = stdout, Error = "TIMEOUT", PeakMemoryMB = peakMB };
        }

        if (captureMemory)
        {
            try
            {
                proc.Refresh();
                peakMB = Math.Max(peakMB, proc.PeakWorkingSet64 / (1024.0 * 1024.0));
            }
            catch
            {
            }
        }

        return new ProcessResult { ExitCode = proc.ExitCode, Output = stdout, Error = stderr, PeakMemoryMB = peakMB };
    }

    static void PrintSummary()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]Test Results[/]"));

        // Results table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Category")
            .AddColumn("Test")
            .AddColumn("SharpImage")
            .AddColumn("ImageMagick")
            .AddColumn("Notes");

        int siPass = 0, siFail = 0, imPass = 0, imFail = 0;
        foreach (var r in results)
        {
            string siStatus = r.SharpImagePassed ? "[green]✓ PASS[/]" : "[red]✗ FAIL[/]";
            string imStatus = r.ImageMagickPassed ? "[green]✓ PASS[/]" : "[red]✗ FAIL[/]";
            table.AddRow(r.Category, r.Name, siStatus, imStatus, r.Notes ?? "");
            if (r.SharpImagePassed)
            {
                siPass++;
            }
            else
            {
                siFail++;
            }

            if (r.ImageMagickPassed)
            {
                imPass++;
            }
            else
            {
                imFail++;
            }
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold]SharpImage:[/] [green]{siPass} passed[/], [red]{siFail} failed[/]");
        AnsiConsole.MarkupLine($"[bold]ImageMagick:[/] [green]{imPass} passed[/], [red]{imFail} failed[/]");

        // Performance table
        if (perfResults.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold blue]Performance Comparison[/]"));

            var perfTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Operation")
                .AddColumn(new TableColumn("SharpImage (ms)").RightAligned())
                .AddColumn(new TableColumn("ImageMagick (ms)").RightAligned())
                .AddColumn(new TableColumn("Ratio").RightAligned())
                .AddColumn(new TableColumn("SI Peak MB").RightAligned())
                .AddColumn(new TableColumn("IM Peak MB").RightAligned());

            foreach (var p in perfResults)
            {
                double ratio = p.ImageMagickMs > 0 ? (double)p.SharpImageMs / p.ImageMagickMs : 0;
                string ratioStr = ratio < 1 ? $"[green]{ratio:F2}× (faster)[/]" : $"[yellow]{ratio:F2}× (slower)[/]";
                perfTable.AddRow(
                    p.Operation,
                    p.SharpImageMs.ToString("N0"),
                    p.ImageMagickMs.ToString("N0"),
                    ratioStr,
                    p.SharpImagePeakMB.ToString("F1"),
                    p.ImageMagickPeakMB.ToString("F1"));
            }
            AnsiConsole.Write(perfTable);
        }
    }

    static void WritePerfCsv()
    {
        if (perfResults.Count == 0)
        {
            return;
        }

        // CSV output
        var csvSb = new StringBuilder();
        csvSb.AppendLine("Operation,SharpImage_ms,ImageMagick_ms,Ratio,SharpImage_PeakMB,ImageMagick_PeakMB");
        foreach (var p in perfResults)
        {
            double ratio = p.ImageMagickMs > 0 ? (double)p.SharpImageMs / p.ImageMagickMs : 0;
            csvSb.AppendLine($"{p.Operation},{p.SharpImageMs},{p.ImageMagickMs},{ratio:F3},{p.SharpImagePeakMB:F1},{p.ImageMagickPeakMB:F1}");
        }
        string csvPath = Path.Combine(BigTestDir, "Performance", "results.csv");
        EnsureDirectoryFor(csvPath);
        File.WriteAllText(csvPath, csvSb.ToString());
        AnsiConsole.MarkupLine($"\n[dim]Performance CSV written to: {csvPath}[/]");

        // Markdown output
        var mdSb = new StringBuilder();
        mdSb.AppendLine("# SharpImage vs ImageMagick Performance Comparison");
        mdSb.AppendLine();
        mdSb.AppendLine($"**Source image**: landscape.jpg → source.png (1500×1000, 16-bit quantum)");
        mdSb.AppendLine($"**Methodology**: Direct exe execution, warmup + median of 5 iterations");
        mdSb.AppendLine($"**SharpImage**: .NET 10 (AOT), 16-bit quantum, SIMD-optimized");
        mdSb.AppendLine($"**ImageMagick**: v7.1.2-15 Q16-HDRI x64 (native C)");
        mdSb.AppendLine();
        mdSb.AppendLine("| Operation | SI (ms) | IM (ms) | Ratio | SI Peak MB | IM Peak MB | Verdict |");
        mdSb.AppendLine("|-----------|--------:|--------:|------:|-----------:|-----------:|---------|");

        int siWins = 0, imWins = 0, ties = 0;
        foreach (var p in perfResults)
        {
            double ratio = p.ImageMagickMs > 0 ? (double)p.SharpImageMs / p.ImageMagickMs : 0;
            string verdict;
            if (ratio <= 0.90)
            {
                verdict = $"**SI {1.0 / ratio:F1}× faster** ✅";
                siWins++;
            }
            else if (ratio >= 1.10)
            {
                verdict = $"SI {ratio:F1}× slower";
                imWins++;
            }
            else
            {
                verdict = "≈ Equal";
                ties++;
            }

            mdSb.AppendLine($"| {p.Operation} | {p.SharpImageMs} | {p.ImageMagickMs} | {ratio:F2}× | {p.SharpImagePeakMB:F1} | {p.ImageMagickPeakMB:F1} | {verdict} |");
        }

        mdSb.AppendLine();
        mdSb.AppendLine($"**Summary**: SharpImage wins {siWins}, ImageMagick wins {imWins}, ties {ties} out of {perfResults.Count} operations.");
        mdSb.AppendLine();
        mdSb.AppendLine("> **Note**: Ratio < 1.0 means SharpImage is faster. Memory includes .NET runtime baseline (~20MB).");

        string mdPath = Path.Combine(BigTestDir, "Performance", "results.md");
        File.WriteAllText(mdPath, mdSb.ToString());
        AnsiConsole.MarkupLine($"[dim]Performance Markdown written to: {mdPath}[/]");
    }

    static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null!)
        {
            if (File.Exists(Path.Combine(dir, "SharpImage.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }
        // Fallback to current directory
        dir = Environment.CurrentDirectory;
        while (dir != null!)
        {
            if (File.Exists(Path.Combine(dir, "SharpImage.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }
        return @"F:\Code\SharpImage";
    }

    // ─────────────────────────────────────────────────────────────
    // TYPES
    // ─────────────────────────────────────────────────────────────

    record TestResult
    {
        public string Category { get; init; } = "";
        public string Name { get; init; } = "";
        public bool SharpImagePassed { get; init; }
        public bool ImageMagickPassed { get; init; }
        public string SharpImageFile { get; init; } = "";
        public string ImageMagickFile { get; init; } = "";
        public string? Notes { get; init; }
        public bool Passed => SharpImagePassed;
    }

    record ProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string Error { get; init; } = "";
        public double PeakMemoryMB { get; init; }
    }

    record PerfResult
    {
        public string Operation { get; init; } = "";
        public long SharpImageMs { get; init; }
        public long ImageMagickMs { get; init; }
        public double SharpImagePeakMB { get; init; }
        public double ImageMagickPeakMB { get; init; }
    }
}
