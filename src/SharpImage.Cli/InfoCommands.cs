// SharpImage CLI — Info and formats commands.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using Spectre.Console;
using System.CommandLine;

namespace SharpImage.Cli;

public static class InfoCommands
{
    public static Command CreateInfoCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var statsOpt = new Option<bool>("--stats") { Description = "Show per-channel statistics (mean, std dev, entropy)" };
        var histOpt = new Option<bool>("--histogram") { Description = "Show ASCII histogram of channel 0 (red/gray)" };
        var cmd = new Command("info", """
            Display detailed information about an image file.
            Shows dimensions, format, channels, bit depth, file size, and optionally
            per-channel statistics and histogram.

            Examples:
              sharpimage info photo.jpg
              sharpimage info photo.png --stats
              sharpimage info photo.png --histogram
            """);
        cmd.Add(inputArg);
        cmd.Add(statsOpt);
        cmd.Add(histOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            bool showStats = parseResult.GetValue(statsOpt);
            bool showHist = parseResult.GetValue(histOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            var fileInfo = new FileInfo(input);
            byte[] data = File.ReadAllBytes(input);
            var format = FormatRegistry.DetectFormat(data);
            if (format == ImageFileFormat.Unknown)
            {
                format = FormatRegistry.DetectFromExtension(input);
            }

            var image = FormatRegistry.Decode(data, format);

            // Basic info table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Property[/]")
                .AddColumn("[bold]Value[/]")
                .Title($"[cyan]Image Info: {Markup.Escape(Path.GetFileName(input))}[/]");

            table.AddRow("File", Markup.Escape(input));
            table.AddRow("Format", $"[green]{format}[/]");
            table.AddRow("Dimensions", $"{image.Columns} × {image.Rows} pixels");
            table.AddRow("Channels", $"{image.NumberOfChannels}{((image.HasAlpha ? " [dim](includes alpha)[/]" : ""))}");
            table.AddRow("Bit Depth", $"{Quantum.Depth}-bit per channel ({Quantum.Depth * image.NumberOfChannels}-bit per pixel)");
            table.AddRow("Colorspace", $"{image.Colorspace}");
            table.AddRow("File Size", FormatFileSize(fileInfo.Length));
            table.AddRow("Pixel Count", $"{image.Columns * image.Rows:N0}");
            table.AddRow("Raw Data Size", FormatFileSize((long)image.Columns * image.Rows * image.NumberOfChannels * 2));

            AnsiConsole.Write(table);

            // Per-channel statistics
            if (showStats)
            {
                AnsiConsole.WriteLine();
                var stats = SharpImage.Analysis.ImageStatistics.GetStatistics(image);

                var statsTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[cyan]Channel Statistics[/]")
                    .AddColumn("[bold]Channel[/]")
                    .AddColumn("[bold]Mean[/]")
                    .AddColumn("[bold]Std Dev[/]")
                    .AddColumn("[bold]Min[/]")
                    .AddColumn("[bold]Max[/]")
                    .AddColumn("[bold]Entropy[/]")
                    .AddColumn("[bold]Eff. Bits[/]");

                string[] channelNames = image.NumberOfChannels switch
                {
                    1 => [ "Gray" ],
                    3 => [ "Red", "Green", "Blue" ],
                    4 => [ "Red", "Green", "Blue", "Alpha" ],
                    _ => Enumerable.Range(0, image.NumberOfChannels).Select(i => $"Ch{i}").ToArray()
                };

                for (int c = 0;c < stats.Channels.Length && c < channelNames.Length;c++)
                {
                    var ch = stats.Channels[c];
                    string name = channelNames[c];
                    string color = name switch
                    {
                        "Red" => "red",
                        "Green" => "green",
                        "Blue" => "blue",
                        "Alpha" => "grey",
                        _ => "white"
                    };
                    statsTable.AddRow(
                        $"[{color}]{name}[/]",
                        $"{ch.Mean:F2}",
                        $"{ch.StandardDeviation:F2}",
                        $"{ch.Minimum:F0}",
                        $"{ch.Maximum:F0}",
                        $"{ch.Entropy:F4}",
                        $"{ch.Depth}"
                    );
                }

                AnsiConsole.Write(statsTable);
            }

            // ASCII histogram
            if (showHist)
            {
                AnsiConsole.WriteLine();
                var hist = SharpImage.Analysis.ImageStatistics.GetHistogram(image, 0);
                long maxCount = hist.Max();
                int barWidth = 60;

                AnsiConsole.MarkupLine("[cyan]Channel 0 Histogram[/] (256 bins)");
                // Show 16 consolidated bins
                for (int bin = 0;bin < 16;bin++)
                {
                    long sum = 0;
                    for (int j = bin * 16;j < (bin + 1) * 16 && j < 256;j++)
                    {
                        sum += hist[j];
                    }

                    int barLen = maxCount > 0 ? (int)(sum * barWidth / (maxCount * 16)) : 0;
                    barLen = Math.Min(barLen, barWidth);
                    string label = $"{bin * 16,3}-{(bin + 1) * 16 - 1,3}";
                    string bar = $"{(new string ('█', barLen))}{(new string ('░', barWidth - barLen))}";
                    AnsiConsole.MarkupLine($"[dim]{label}[/] [cyan]{bar}[/] {sum:N0}");
                }
            }
        });
        return cmd;
    }

    public static Command CreateFormatsCommand()
    {
        var cmd = new Command("formats", """
            List all supported image formats with read/write capabilities.

            Examples:
              sharpimage formats
            """);
        cmd.SetAction((_) =>
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[cyan]Supported Image Formats[/]")
                .AddColumn("[bold]Format[/]")
                .AddColumn("[bold]Extensions[/]")
                .AddColumn("[bold]Read[/]")
                .AddColumn("[bold]Write[/]")
                .AddColumn("[bold]Description[/]");

            AddFormatRow(table, "PNG", ".png", true, true, "Portable Network Graphics — lossless, alpha");
            AddFormatRow(table, "JPEG", ".jpg .jpeg", true, true, "JPEG — lossy compression, photos");
            AddFormatRow(table, "GIF", ".gif", true, true, "Graphics Interchange Format — 256 colors, animation");
            AddFormatRow(table, "BMP", ".bmp", true, true, "Windows Bitmap — 1/4/8/16/24/32-bit, RLE");
            AddFormatRow(table, "TGA", ".tga", true, true, "Targa — true-color, RLE, alpha");
            AddFormatRow(table, "PNM", ".pnm .ppm .pgm .pbm", true, true, "Portable Anymap — ASCII/binary");
            AddFormatRow(table, "TIFF", ".tif .tiff", true, true, "Tagged Image File Format — LZW, deflate, PackBits");
            AddFormatRow(table, "WebP", ".webp", true, true, "WebP — VP8L lossless + VP8 lossy");
            AddFormatRow(table, "QOI", ".qoi", true, true, "Quite OK Image — fast lossless");
            AddFormatRow(table, "ICO", ".ico", true, true, "Windows Icon — multi-size");
            AddFormatRow(table, "HDR", ".hdr .pic", true, true, "Radiance RGBE — high dynamic range");
            AddFormatRow(table, "PSD", ".psd", true, true, "Adobe Photoshop — composite image");
            AddFormatRow(table, "DDS", ".dds", true, true, "DirectDraw Surface — DXT1/DXT5/BC7");
            AddFormatRow(table, "SVG", ".svg", true, true, "Scalable Vector Graphics — rasterizer");
            AddFormatRow(table, "Farbfeld", ".ff .farbfeld", true, true, "Farbfeld — 16-bit RGBA");
            AddFormatRow(table, "WBMP", ".wbmp", true, true, "Wireless Bitmap — 1-bit");
            AddFormatRow(table, "PCX", ".pcx", true, true, "PCX — RLE, 8/24-bit");
            AddFormatRow(table, "XBM", ".xbm", true, true, "X BitMap — text C header");
            AddFormatRow(table, "XPM", ".xpm", true, true, "X PixMap — text C source");
            AddFormatRow(table, "DPX", ".dpx", true, true, "Digital Picture Exchange — film, 10-bit");
            AddFormatRow(table, "FITS", ".fits .fit .fts", true, true, "FITS — astronomy, multi-bit");
            AddFormatRow(table, "CIN", ".cin .cineon", true, true, "Cineon — film, 10-bit packed");
            AddFormatRow(table, "DICOM", ".dcm .dicom", true, true, "DICOM — medical imaging");
            AddFormatRow(table, "JPEG 2000", ".jp2 .j2k .j2c", true, true, "JPEG 2000 — wavelet-based");
            AddFormatRow(table, "JPEG XL", ".jxl", true, true, "JPEG XL — modern codec");
            AddFormatRow(table, "AVIF", ".avif", true, true, "AVIF — AV1 still image");
            AddFormatRow(table, "HEIC", ".heic .heif", true, true, "HEIC — HEVC still image");
            AddFormatRow(table, "EXR", ".exr", true, true, "OpenEXR — HDR, VFX/film industry");

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[bold]Total: [cyan]28[/] formats supported[/]");
        });
        return cmd;
    }

    private static void AddFormatRow(Table table, string name, string ext, bool read, bool write, string desc)
    {
        table.AddRow(
            $"[bold]{name}[/]",
            $"[dim]{ext}[/]",
            read ? "[green]✓[/]" : "[red]✗[/]",
            write ? "[green]✓[/]" : "[red]✗[/]",
            desc);
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    public static Command CreatePsdLayersCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input PSD file path" };
        var outputDirOpt = new Option<string>("--output-dir") { Description = "Output directory for extracted layers", DefaultValueFactory = _ => "." };
        var formatOpt = new Option<string>("--format") { Description = "Output format for extracted layers (png, tiff, bmp)", DefaultValueFactory = _ => "png" };

        var cmd = new Command("psdlayers", """
            Extract individual layers from a PSD file.
            Each layer is saved as a separate image with position metadata.
            
            Examples:
              sharpimage psdlayers design.psd
              sharpimage psdlayers design.psd --output-dir layers/ --format tiff
            """);
        cmd.Add(inputArg);
        cmd.Add(outputDirOpt);
        cmd.Add(formatOpt);

        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string outputDir = parseResult.GetValue(outputDirOpt)!;
            string format = parseResult.GetValue(formatOpt)!;

            if (!CliOutput.ValidateInputExists(input)) return;
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            AnsiConsole.MarkupLine($"[cyan]Reading PSD:[/] {Path.GetFileName(input)}");
            byte[] data = File.ReadAllBytes(input);
            var doc = PsdCoder.DecodeWithLayers(data);

            AnsiConsole.MarkupLine($"[green]Canvas:[/] {doc.Composite.Columns}×{doc.Composite.Rows}");
            AnsiConsole.MarkupLine($"[green]Layers:[/] {doc.Layers.Count}");

            var table = new Table().AddColumn("Layer").AddColumn("Size").AddColumn("Offset")
                .AddColumn("Opacity").AddColumn("Blend").AddColumn("Visible");

            for (int i = 0; i < doc.Layers.Count; i++)
            {
                var layer = doc.Layers[i];
                string safeName = string.Join("_", layer.Name.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrEmpty(safeName)) safeName = $"layer_{i}";

                table.AddRow(
                    layer.Name,
                    $"{layer.Width}×{layer.Height}",
                    $"({layer.Left}, {layer.Top})",
                    $"{layer.Opacity}",
                    layer.BlendMode,
                    layer.Visible ? "✓" : "✗");

                if (layer.Image != null && layer.Width > 0 && layer.Height > 0)
                {
                    string outputFile = Path.Combine(outputDir, $"{safeName}.{format}");
                    FormatRegistry.Write(layer.Image, outputFile);
                    AnsiConsole.MarkupLine($"  [dim]→ {outputFile}[/]");
                }
            }

            AnsiConsole.Write(table);
            doc.Composite.Dispose();
            foreach (var l in doc.Layers) l.Image?.Dispose();
        });
        return cmd;
    }
}