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
            AddFormatRow(table, "SGI", ".sgi .rgb .bw", true, true, "SGI — Silicon Graphics image");
            AddFormatRow(table, "PIX", ".pix", true, true, "PIX — Alias/WaveFront image");
            AddFormatRow(table, "Sun", ".sun .ras", true, true, "Sun Raster — Sun Microsystems");
            AddFormatRow(table, "PDF", ".pdf", false, true, "PDF — rasterized page export");

            // Camera Raw formats
            table.AddEmptyRow();
            table.AddRow("[bold yellow]Camera Raw[/]", "", "", "", "[dim]31 manufacturer-specific raw formats[/]");
            AddFormatRow(table, "DNG", ".dng", true, true, "Adobe Digital Negative — open raw standard");
            AddFormatRow(table, "CR2", ".cr2", true, false, "Canon RAW 2 — Canon EOS DSLR");
            AddFormatRow(table, "CR3", ".cr3", true, false, "Canon RAW 3 — Canon EOS R mirrorless");
            AddFormatRow(table, "CRW", ".crw", true, false, "Canon RAW (CIFF) — older Canon");
            AddFormatRow(table, "NEF", ".nef .nrw", true, false, "Nikon Electronic Format — Nikon DSLR/Z");
            AddFormatRow(table, "ARW", ".arw .sr2 .srf", true, false, "Sony Alpha RAW — Sony mirrorless/DSLR");
            AddFormatRow(table, "ORF", ".orf", true, false, "Olympus RAW Format — Olympus/OM System");
            AddFormatRow(table, "RW2", ".rw2", true, false, "Panasonic RAW — Lumix cameras");
            AddFormatRow(table, "RAF", ".raf", true, false, "Fuji RAW — Fujifilm X-Trans/Bayer");
            AddFormatRow(table, "PEF", ".pef", true, false, "Pentax Electronic Format — Pentax DSLR");
            AddFormatRow(table, "SRW", ".srw", true, false, "Samsung RAW — Samsung NX cameras");
            AddFormatRow(table, "3FR", ".3fr", true, false, "Hasselblad 3F RAW — Hasselblad medium format");
            AddFormatRow(table, "IIQ", ".iiq", true, false, "Phase One — medium format digital back");
            AddFormatRow(table, "X3F", ".x3f", true, false, "Sigma/Foveon — Foveon X3 sensor");
            AddFormatRow(table, "DCR/KDC", ".dcr .k25 .kdc", true, false, "Kodak RAW — Kodak DSLRs");
            AddFormatRow(table, "MRW", ".mrw", true, false, "Minolta RAW — Konica Minolta");
            AddFormatRow(table, "MEF", ".mef", true, false, "Mamiya Electronic Format — Mamiya/Phase One");
            AddFormatRow(table, "MOS", ".mos", true, false, "Leaf MOS — Leaf digital backs");
            AddFormatRow(table, "ERF", ".erf", true, false, "Epson RAW — Epson R-D1");
            AddFormatRow(table, "FFF", ".fff", true, false, "Hasselblad Flexible File Format");
            AddFormatRow(table, "Others", ".mdc .rmf .rwl .sti", true, false, "Minolta MDC, Ricoh, Sinar raw formats");

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[bold]Total: [cyan]34[/] format families ([cyan]31[/] camera raw variants) supported[/]");
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

    public static Command CreateRawInfoCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Camera raw file path (CR2, NEF, ARW, DNG, RAF, etc.)" };
        var cmd = new Command("rawinfo", """
            Display detailed camera raw metadata without fully decoding the image.
            Shows manufacturer, camera model, sensor dimensions, Bayer pattern,
            bit depth, compression, exposure settings, and white balance data.

            Examples:
              sharpimage rawinfo photo.cr2
              sharpimage rawinfo photo.nef
              sharpimage rawinfo photo.dng
            """);
        cmd.Add(inputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            if (!CliOutput.ValidateInputExists(input))
                return;

            byte[] data = File.ReadAllBytes(input);
            if (!CameraRawCoder.CanDecode(data))
            {
                var extFormat = FormatRegistry.DetectFromExtension(input);
                if (extFormat != ImageFileFormat.CameraRaw)
                {
                    CliOutput.PrintError("Not a recognized camera raw format.");
                    return;
                }
            }

            CameraRawMetadata meta;
            try
            {
                meta = CameraRawCoder.GetMetadata(data);
            }
            catch (Exception ex)
            {
                CliOutput.PrintError($"Failed to parse raw metadata: {ex.Message}");
                return;
            }

            var fileInfo = new FileInfo(input);
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Property[/]")
                .AddColumn("[bold]Value[/]")
                .Title($"[cyan]Camera Raw Info: {Markup.Escape(Path.GetFileName(input))}[/]");

            table.AddRow("File", Markup.Escape(input));
            table.AddRow("File Size", FormatFileSize(fileInfo.Length));
            table.AddRow("Format", $"[green]{meta.Format}[/]");

            table.AddRow("[bold]Camera[/]", "");
            table.AddRow("  Make", string.IsNullOrEmpty(meta.Make) ? "[dim]unknown[/]" : Markup.Escape(meta.Make));
            table.AddRow("  Model", string.IsNullOrEmpty(meta.Model) ? "[dim]unknown[/]" : Markup.Escape(meta.Model));

            table.AddRow("[bold]Sensor[/]", "");
            table.AddRow("  Sensor Size", $"{meta.SensorWidth} × {meta.SensorHeight} pixels");
            table.AddRow("  Active Area", $"{meta.ActiveAreaWidth} × {meta.ActiveAreaHeight} pixels");
            if (meta.ActiveAreaLeft != 0 || meta.ActiveAreaTop != 0)
                table.AddRow("  Active Offset", $"({meta.ActiveAreaLeft}, {meta.ActiveAreaTop})");
            table.AddRow("  Megapixels", $"{(double)meta.ActiveAreaWidth * meta.ActiveAreaHeight / 1_000_000:F1} MP");
            table.AddRow("  CFA Type", $"{meta.CfaType}");
            if (meta.CfaType == CfaType.Bayer)
                table.AddRow("  Bayer Pattern", $"{meta.BayerPattern}");
            table.AddRow("  Bits Per Sample", $"{meta.BitsPerSample}");
            table.AddRow("  Compression", $"{meta.Compression}");

            table.AddRow("[bold]Levels[/]", "");
            table.AddRow("  Black Level", meta.BlackLevelPerChannel is not null
                ? string.Join(", ", meta.BlackLevelPerChannel)
                : $"{meta.BlackLevel}");
            table.AddRow("  White Level", $"{meta.WhiteLevel}");

            if (meta.IsoSpeed > 0 || meta.ExposureTime > 0 || meta.FNumber > 0)
            {
                table.AddRow("[bold]Exposure[/]", "");
                if (meta.IsoSpeed > 0) table.AddRow("  ISO", $"{meta.IsoSpeed}");
                if (meta.ExposureTime > 0)
                {
                    string shutterStr = meta.ExposureTime >= 1
                        ? $"{meta.ExposureTime:F1}s"
                        : $"1/{1.0 / meta.ExposureTime:F0}s";
                    table.AddRow("  Shutter Speed", shutterStr);
                }
                if (meta.FNumber > 0) table.AddRow("  Aperture", $"f/{meta.FNumber:F1}");
                if (meta.FocalLength > 0) table.AddRow("  Focal Length", $"{meta.FocalLength:F1} mm");
            }

            if (meta.AsShotWhiteBalance is not null)
            {
                table.AddRow("[bold]White Balance[/]", "");
                table.AddRow("  As-Shot WB", string.Join(", ", meta.AsShotWhiteBalance.Select(v => v.ToString("F4"))));
                if (meta.DaylightWhiteBalance is not null)
                    table.AddRow("  Daylight WB", string.Join(", ", meta.DaylightWhiteBalance.Select(v => v.ToString("F4"))));
            }

            table.AddRow("[bold]Raw Data[/]", "");
            table.AddRow("  Data Offset", $"{meta.RawDataOffset:N0} bytes");
            table.AddRow("  Data Length", $"{meta.RawDataLength:N0} bytes ({FormatFileSize(meta.RawDataLength)})");
            if (meta.ThumbnailLength > 0)
                table.AddRow("  Thumbnail", $"offset {meta.ThumbnailOffset:N0}, {FormatFileSize(meta.ThumbnailLength)}");
            table.AddRow("  Orientation", $"{meta.Orientation} ({OrientationDescription(meta.Orientation)})");

            AnsiConsole.Write(table);
        });
        return cmd;
    }

    private static string OrientationDescription(int orientation) => orientation switch
    {
        1 => "Normal",
        2 => "Mirrored horizontal",
        3 => "Rotated 180°",
        4 => "Mirrored vertical",
        5 => "Mirrored horizontal + rotated 270°",
        6 => "Rotated 90° CW",
        7 => "Mirrored horizontal + rotated 90°",
        8 => "Rotated 270° CW",
        _ => "Unknown"
    };

}