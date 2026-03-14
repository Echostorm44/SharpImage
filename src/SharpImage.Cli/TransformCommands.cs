// SharpImage CLI — Transform commands: convert, resize, crop, rotate, flip, flop, distort.

using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;
using System.CommandLine;

namespace SharpImage.Cli;

public static class TransformCommands
{
    public static Command CreateConvertCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path (format determined by extension)" };
        var qualityOption = new Option<int>("--quality") { Description = "Encoding quality 1-100 for lossy formats like JXL (default: 100 = lossless)", DefaultValueFactory = _ => 100 };
        var bc7Option = new Option<bool>("--bc7") { Description = "Use BC7 compression for DDS output (default: uncompressed BGRA32)" };
        var cmd = new Command("convert", """
            Convert an image from one format to another.
            The output format is automatically determined from the file extension.
            Supports 33 formats: PNG, JPEG, GIF, BMP, TGA, PNM, TIFF, WebP, QOI, ICO,
            HDR, PSD, DDS, SVG, Farbfeld, WBMP, PCX, XBM, XPM, DPX, FITS, CIN,
            DICOM, JPEG 2000, JPEG XL, AVIF, HEIC, OpenEXR, SGI, PIX, Sun, PDF.

            Examples:
              sharpimage convert photo.jpg photo.png
              sharpimage convert scan.tiff output.webp
              sharpimage convert image.bmp image.jxl --quality 75
              sharpimage convert image.png texture.dds --bc7
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Options.Add(qualityOption);
        cmd.Options.Add(bc7Option);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int quality = parseResult.GetValue(qualityOption);
            bool useBc7 = parseResult.GetValue(bc7Option);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            if (useBc7 && output.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                CliOutput.RunPipelineCustomWrite(input, output, "Converting", img => img,
                    img => DdsCoder.EncodeBc7(img));
            }
            else if (quality < 100 && output.EndsWith(".jxl", StringComparison.OrdinalIgnoreCase))
            {
                CliOutput.RunPipeline(input, output, "Converting", img => img, quality);
            }
            else
            {
                CliOutput.RunPipeline(input, output, "Converting", img => img);
            }
        });
        return cmd;
    }

    public static Command CreateProgressiveJpegCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output progressive JPEG file path" };
        var qualityOption = new Option<int>("--quality") { Description = "JPEG quality 1-100 (default: 85)", DefaultValueFactory = _ => 85 };

        var cmd = new Command("progressive", """
            Write a progressive (interlaced) JPEG.
            Progressive JPEGs load in multiple passes, showing a low-quality preview first.
            This is preferred for web delivery where perceived load time matters.

            Examples:
              sharpimage progressive photo.png photo.jpg
              sharpimage progressive image.bmp output.jpg --quality 90
            """);
        cmd.Arguments.Add(inputArg);
        cmd.Arguments.Add(outputArg);
        cmd.Options.Add(qualityOption);

        cmd.SetAction(parseResult =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int quality = parseResult.GetValue(qualityOption);

            if (!CliOutput.ValidateInputExists(input))
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var image = FormatRegistry.Read(input);
            JpegCoder.WriteProgressive(image, output, quality);
            sw.Stop();
            CliOutput.PrintSuccess(input, output, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateResizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "Target width in pixels", Required = true };
        var heightOpt = new Option<int>("-h", "--height") { Description = "Target height in pixels", Required = true };
        var methodOpt = new Option<string>("-m", "--method") { Description = "Interpolation method (see list below)", DefaultValueFactory = _ => "lanczos3" };
        var cmd = new Command("resize", """
            Resize an image to specified dimensions.
            Supports 23 interpolation methods for quality/speed tradeoff.

            Methods:
              nearest    — Fastest, pixelated (good for pixel art)
              bilinear   — Linear interpolation (= triangle = bartlett)
              triangle   — Linear interpolation (alias for bilinear)
              hermite    — Cubic Hermite, smooth, no overshoot
              bicubic    — Catmull-Rom spline (= catrom)
              catrom     — Catmull-Rom (B=0, C=0.5), sharp with slight ringing
              mitchell   — Mitchell-Netravali (B=1/3, C=1/3), balanced
              gaussian   — Gaussian bell curve, smooth
              spline     — B-Spline (B=1, C=0), very smooth, no ringing
              sinc       — Truncated sinc, some ringing
              lanczos2   — Lanczos with 2-lobe support
              lanczos3   — Lanczos with 3-lobe support (default, highest quality)
              lanczos4   — Lanczos with 4-lobe support
              lanczos5   — Lanczos with 5-lobe support
              hann       — Hann-windowed sinc
              hamming    — Hamming-windowed sinc
              blackman   — Blackman-windowed sinc
              kaiser     — Kaiser-Bessel-windowed sinc
              parzen     — Parzen / de la Vallée-Poussin
              bohman     — Bohman window, smooth taper
              bartlett   — Bartlett (alias for triangle)
              welch      — Welch parabolic window
              lagrange   — Lagrange 3rd-order polynomial

            Examples:
              sharpimage resize photo.jpg -w 800 -h 600 output.png
              sharpimage resize icon.png -w 64 -h 64 --method nearest small.png
              sharpimage resize large.tiff -w 1920 -h 1080 --method mitchell hd.jpg
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);
        cmd.Add(methodOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            string method = parseResult.GetValue(methodOpt)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            var interp = ParseInterpolationMethod(method);
            CliOutput.RunPipeline(input, output, $"Resizing to {w}×{h} ({method})",
                img => Resize.Apply(img, w, h, interp));
        });
        return cmd;
    }

    private static InterpolationMethod ParseInterpolationMethod(string method)
    {
        return method.ToLowerInvariant() switch
        {
            "nearest" => InterpolationMethod.NearestNeighbor,
            "bilinear" => InterpolationMethod.Bilinear,
            "triangle" => InterpolationMethod.Triangle,
            "bartlett" => InterpolationMethod.Bartlett,
            "hermite" => InterpolationMethod.Hermite,
            "bicubic" => InterpolationMethod.Bicubic,
            "catrom" => InterpolationMethod.Catrom,
            "mitchell" => InterpolationMethod.Mitchell,
            "gaussian" => InterpolationMethod.Gaussian,
            "spline" => InterpolationMethod.Spline,
            "sinc" => InterpolationMethod.Sinc,
            "lanczos2" => InterpolationMethod.Lanczos2,
            "lanczos" or "lanczos3" => InterpolationMethod.Lanczos3,
            "lanczos4" => InterpolationMethod.Lanczos4,
            "lanczos5" => InterpolationMethod.Lanczos5,
            "hann" => InterpolationMethod.Hann,
            "hamming" => InterpolationMethod.Hamming,
            "blackman" => InterpolationMethod.Blackman,
            "kaiser" => InterpolationMethod.Kaiser,
            "parzen" => InterpolationMethod.Parzen,
            "bohman" => InterpolationMethod.Bohman,
            "welch" => InterpolationMethod.Welch,
            "lagrange" => InterpolationMethod.Lagrange,
            _ => InterpolationMethod.Lanczos3
        };
    }

    public static Command CreateCropCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<int>("-x", "--left") { Description = "Left offset in pixels", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("-y", "--top") { Description = "Top offset in pixels", DefaultValueFactory = _ => 0 };
        var wOpt = new Option<int>("-w", "--width") { Description = "Crop width in pixels", Required = true };
        var hOpt = new Option<int>("-h", "--height") { Description = "Crop height in pixels", Required = true };
        var cmd = new Command("crop", """
            Crop a rectangular region from an image.
            Coordinates are measured from the top-left corner.

            Examples:
              sharpimage crop photo.jpg -x 100 -y 50 -w 400 -h 300 cropped.png
              sharpimage crop scan.png -w 200 -h 200 thumbnail.jpg
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.Add(wOpt);
        cmd.Add(hOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            int w = parseResult.GetValue(wOpt);
            int h = parseResult.GetValue(hOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Cropping {w}×{h} at ({x},{y})",
                img => Geometry.Crop(img, x, y, w, h));
        });
        return cmd;
    }

    public static Command CreateRotateCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var angleOpt = new Option<double>("-a", "--angle") { Description = "Rotation angle in degrees (positive = clockwise)", Required = true };
        var cmd = new Command("rotate", """
            Rotate an image by a specified angle.
            For 90°, 180°, 270° — uses fast transpose (no interpolation).
            For other angles — uses bilinear interpolation with bounding box expansion.

            Examples:
              sharpimage rotate photo.jpg --angle 90 rotated.png
              sharpimage rotate scan.png --angle 180 flipped.png
              sharpimage rotate art.bmp --angle 45 tilted.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(angleOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double angle = parseResult.GetValue(angleOpt);
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Rotating {angle}°", img =>
            {
                return angle switch
                {
                    90.0 => Geometry.Rotate(img, RotationAngle.Rotate90),
                    180.0 => Geometry.Rotate(img, RotationAngle.Rotate180),
                    270.0 => Geometry.Rotate(img, RotationAngle.Rotate270),
                    _ => Geometry.RotateArbitrary(img, angle)
                };
            });
        });
        return cmd;
    }

    public static Command CreateFlipCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("flip", """
            Flip an image vertically (mirror top-to-bottom).

            Examples:
              sharpimage flip photo.jpg flipped.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Flipping vertically", Geometry.Flip);
        });
        return cmd;
    }

    public static Command CreateFlopCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("flop", """
            Flop an image horizontally (mirror left-to-right).

            Examples:
              sharpimage flop photo.jpg flopped.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, "Flopping horizontally", Geometry.Flop);
        });
        return cmd;
    }

    public static Command CreateDistortCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var typeOpt = new Option<string>("-t", "--type") { Description = "Distortion type: barrel, polar, cartesian", DefaultValueFactory = _ => "barrel" };
        var aOpt = new Option<double>("-a") { Description = "Barrel coefficient a (radial)", DefaultValueFactory = _ => 0.0 };
        var bOpt = new Option<double>("-b") { Description = "Barrel coefficient b (radial)", DefaultValueFactory = _ => 0.0 };
        var cOpt = new Option<double>("-c") { Description = "Barrel coefficient c (radial)", DefaultValueFactory = _ => 1.0 };
        var dOpt = new Option<double>("-d") { Description = "Barrel coefficient d (tangential)", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("distort", """
            Apply geometric distortion to an image.

            Types:
              barrel     — Barrel/pincushion distortion (lens correction)
              polar      — Convert Cartesian to polar coordinates
              cartesian  — Convert polar back to Cartesian coordinates

            For barrel distortion, coefficients control the curve:
              a,b,c,d where r' = a*r³ + b*r² + c*r + d

            Examples:
              sharpimage distort photo.jpg -t barrel -a 0.1 -b -0.2 corrected.png
              sharpimage distort image.png -t polar polar.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(typeOpt);
        cmd.Add(aOpt);
        cmd.Add(bOpt);
        cmd.Add(cOpt);
        cmd.Add(dOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string type = parseResult.GetValue(typeOpt)!;
            if (!CliOutput.ValidateInputExists(input))
            {
                return;
            }

            CliOutput.RunPipeline(input, output, $"Distorting ({type})", img =>
            {
                return type.ToLowerInvariant() switch
                {
                    "barrel" => Distort.Barrel(img,
                        parseResult.GetValue(aOpt), parseResult.GetValue(bOpt),
                        parseResult.GetValue(cOpt), parseResult.GetValue(dOpt)),
                    "polar" => Distort.CartesianToPolar(img),
                    "cartesian" => Distort.PolarToCartesian(img),
                    _ => throw new ArgumentException($"Unknown distortion type: {type}")
                };
            });
        });
        return cmd;
    }

    public static Command CreateTrimCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var fuzzOpt = new Option<double>("-f", "--fuzz") { Description = "Color distance tolerance (0.0–1.0). Higher = more aggressive trim", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("trim", """
            Remove uniform borders from an image.
            Detects the border color from corner pixels and removes matching edges.
            Use --fuzz to allow near-matching colors (useful for JPEG artifacts).

            Examples:
              sharpimage trim scan.png trimmed.png
              sharpimage trim photo.jpg --fuzz 0.05 trimmed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(fuzzOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double fuzz = parseResult.GetValue(fuzzOpt);
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, $"Trimming (fuzz={fuzz:F2})",
                img => Geometry.Trim(img, fuzz));
        });
        return cmd;
    }

    public static Command CreateExtentCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var widthOpt = new Option<int>("-w", "--width") { Description = "New canvas width", Required = true };
        var heightOpt = new Option<int>("-h", "--height") { Description = "New canvas height", Required = true };
        var xOpt = new Option<int>("-x", "--offset-x") { Description = "X position of original on canvas", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("-y", "--offset-y") { Description = "Y position of original on canvas", DefaultValueFactory = _ => 0 };
        var cmd = new Command("extent", """
            Extend the image canvas to new dimensions.
            The original image is placed at the specified offset. New areas are black/transparent.

            Examples:
              sharpimage extent photo.jpg -w 1920 -h 1080 padded.png
              sharpimage extent icon.png -w 256 -h 256 -x 64 -y 64 centered.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, $"Extending canvas to {w}×{h}",
                img => Geometry.Extent(img, w, h, x, y));
        });
        return cmd;
    }

    public static Command CreateShaveCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var hOpt = new Option<int>("--horizontal") { Description = "Pixels to remove from left and right edges", DefaultValueFactory = _ => 0 };
        var vOpt = new Option<int>("--vertical") { Description = "Pixels to remove from top and bottom edges", DefaultValueFactory = _ => 0 };
        var cmd = new Command("shave", """
            Remove equal pixel strips from all four edges of an image.
            Removes --horizontal pixels from left AND right, --vertical from top AND bottom.

            Examples:
              sharpimage shave photo.jpg --horizontal 10 --vertical 20 shaved.png
              sharpimage shave scan.png --horizontal 50 trimmed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(hOpt);
        cmd.Add(vOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int h = parseResult.GetValue(hOpt);
            int v = parseResult.GetValue(vOpt);
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, $"Shaving {h}px H, {v}px V",
                img => Geometry.Shave(img, h, v));
        });
        return cmd;
    }

    public static Command CreateChopCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var xOpt = new Option<int>("-x") { Description = "X start of region to remove", DefaultValueFactory = _ => 0 };
        var yOpt = new Option<int>("-y") { Description = "Y start of region to remove", DefaultValueFactory = _ => 0 };
        var wOpt = new Option<int>("-w", "--width") { Description = "Width of region to remove", Required = true };
        var hOpt = new Option<int>("-h", "--height") { Description = "Height of region to remove", Required = true };
        var cmd = new Command("chop", """
            Remove a rectangular region from an image and collapse remaining pixels.
            The surrounding image collapses to fill the removed area.

            Examples:
              sharpimage chop photo.jpg -x 100 -y 0 -w 50 -h 600 chopped.png
              sharpimage chop banner.png -x 0 -y 200 -w 800 -h 100 no-strip.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(xOpt);
        cmd.Add(yOpt);
        cmd.Add(wOpt);
        cmd.Add(hOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int x = parseResult.GetValue(xOpt);
            int y = parseResult.GetValue(yOpt);
            int w = parseResult.GetValue(wOpt);
            int h = parseResult.GetValue(hOpt);
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, $"Chopping {w}×{h} at ({x},{y})",
                img => Geometry.Chop(img, x, y, w, h));
        });
        return cmd;
    }

    public static Command CreateTransposeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("transpose", """
            Transpose an image by reflecting about the main diagonal (top-left to bottom-right).
            Width and height are swapped. Pixel at (x,y) moves to (y,x).

            Examples:
              sharpimage transpose photo.jpg transposed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, "Transposing", Geometry.Transpose);
        });
        return cmd;
    }

    public static Command CreateTransverseCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("transverse", """
            Reflect an image about the anti-diagonal (top-right to bottom-left).
            Width and height are swapped. Pixel at (x,y) moves to (H-1-y, W-1-x).

            Examples:
              sharpimage transverse photo.jpg transversed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, "Transversing", Geometry.Transverse);
        });
        return cmd;
    }

    public static Command CreateDeskewCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var threshOpt = new Option<double>("-t", "--threshold") { Description = "Brightness threshold (0.0–1.0) for foreground detection. Default 0.4", DefaultValueFactory = _ => 0.4 };
        var cmd = new Command("deskew", """
            Detect and correct image skew (rotation) automatically.
            Uses Radon transform projection analysis to find the skew angle.
            Best results on scanned text documents or images with strong horizontal/vertical features.

            Examples:
              sharpimage deskew scan.png straightened.png
              sharpimage deskew tilted.jpg --threshold 0.5 fixed.png
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(threshOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double threshold = parseResult.GetValue(threshOpt);
            if (!CliOutput.ValidateInputExists(input))
                return;

            CliOutput.RunPipeline(input, output, $"Deskewing (threshold={threshold:F2})",
                img => Geometry.Deskew(img, threshold));
        });
        return cmd;
    }

    public static Command CreatePdfExportCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output PDF file path" };
        var compressionOption = new Option<string>("--compression")
        { Description = "Image compression: deflate (lossless, default) or jpeg (lossy)", DefaultValueFactory = _ => "deflate" };
        var qualityOption = new Option<int>("--quality")
        { Description = "JPEG quality 1-100 when using --compression jpeg (default: 85)", DefaultValueFactory = _ => 85 };
        var dpiOption = new Option<double>("--dpi")
        { Description = "Resolution for page sizing — higher DPI = smaller page (default: 72)", DefaultValueFactory = _ => 72.0 };
        var titleOption = new Option<string?>("--title") { Description = "PDF document title metadata" };

        var cmd = new Command("topdf", """
            Export an image to PDF format.
            Creates a single-page PDF containing the image. Supports Deflate (lossless)
            or JPEG (lossy) compression for the embedded image data.
            
            Page size is computed from the image dimensions and DPI setting:
              At 72 DPI (default), 1 pixel = 1 PDF point.
              At 144 DPI, page is half-size (2 pixels per point).
              At 300 DPI, page is ~quarter-size (for print-resolution output).
            
            Examples:
              sharpimage topdf photo.jpg photo.pdf
              sharpimage topdf photo.png output.pdf --compression jpeg --quality 90
              sharpimage topdf scan.tiff print.pdf --dpi 300
              sharpimage topdf image.png doc.pdf --title "My Image"
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Options.Add(compressionOption);
        cmd.Options.Add(qualityOption);
        cmd.Options.Add(dpiOption);
        cmd.Options.Add(titleOption);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            string compression = parseResult.GetValue(compressionOption)!;
            int quality = parseResult.GetValue(qualityOption);
            double dpi = parseResult.GetValue(dpiOption);
            string? title = parseResult.GetValue(titleOption);

            if (!CliOutput.ValidateInputExists(input)) return;

            var pdfCompression = compression.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
                ? PdfCompression.Jpeg : PdfCompression.Deflate;
            var options = new PdfWriteOptions
            {
                Compression = pdfCompression,
                JpegQuality = quality,
                Dpi = dpi,
                Title = title
            };

            CliOutput.RunPipelineCustomWrite(input, output, $"Exporting to PDF ({compression})",
                img => img, img => PdfCoder.Encode(img, options));
        });
        return cmd;
    }

    // ─── Bundle D: Lens Corrections CLI commands ────────────────────

    public static Command CreateChromaticAberrationCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var redOpt = new Option<double>("--red-shift") { Description = "Red channel radial shift", DefaultValueFactory = _ => 0.001 };
        var blueOpt = new Option<double>("--blue-shift") { Description = "Blue channel radial shift", DefaultValueFactory = _ => -0.001 };
        var cmd = new Command("cafix", "Fix chromatic aberration by per-channel radial correction.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(redOpt); cmd.Add(blueOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double red = parseResult.GetValue(redOpt);
            double blue = parseResult.GetValue(blueOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Chromatic Aberration Fix",
                img => Distort.ChromaticAberrationFix(img, red, blue));
        });
        return cmd;
    }

    public static Command CreateVignetteCorrectionCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var strengthOpt = new Option<double>("--strength") { Description = "Correction strength (0-2)", DefaultValueFactory = _ => 1.0 };
        var midpointOpt = new Option<double>("--midpoint") { Description = "Falloff midpoint (0-1)", DefaultValueFactory = _ => 0.5 };
        var cmd = new Command("vigcorrect", "Correct vignette (radial brightness falloff).");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(strengthOpt); cmd.Add(midpointOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double strength = parseResult.GetValue(strengthOpt);
            double midpoint = parseResult.GetValue(midpointOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Vignette Correction",
                img => Distort.VignetteCorrection(img, strength, midpoint));
        });
        return cmd;
    }

    public static Command CreatePerspectiveCorrectionCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var tiltXOpt = new Option<double>("--tilt-x") { Description = "Horizontal tilt correction in degrees", DefaultValueFactory = _ => 0.0 };
        var tiltYOpt = new Option<double>("--tilt-y") { Description = "Vertical tilt correction in degrees", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("perspcorrect", "Correct perspective tilt distortion.");
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Add(tiltXOpt); cmd.Add(tiltYOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double tx = parseResult.GetValue(tiltXOpt);
            double ty = parseResult.GetValue(tiltYOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Perspective Correction",
                img => Distort.PerspectiveCorrection(img, tx, ty));
        });
        return cmd;
    }

    // ─── Bundle J: Advanced Transform CLI commands ──────────────────

    public static Command CreateLiquifyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var outputArg = new Argument<string>("output") { Description = "Output image" };
        var cxOpt = new Option<int>("--cx") { Description = "Center X", Required = true };
        var cyOpt = new Option<int>("--cy") { Description = "Center Y", Required = true };
        var radiusOpt = new Option<int>("--radius") { Description = "Effect radius", DefaultValueFactory = _ => 30 };
        var strengthOpt = new Option<double>("--strength") { Description = "Warp strength", DefaultValueFactory = _ => 0.8 };
        var dxOpt = new Option<double>("--dx") { Description = "Push direction X", DefaultValueFactory = _ => 10.0 };
        var dyOpt = new Option<double>("--dy") { Description = "Push direction Y", DefaultValueFactory = _ => 0.0 };
        var cmd = new Command("liquify", "Apply forward warp (liquify) deformation.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(cxOpt); cmd.Add(cyOpt); cmd.Add(radiusOpt); cmd.Add(strengthOpt);
        cmd.Add(dxOpt); cmd.Add(dyOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int cx = parseResult.GetValue(cxOpt);
            int cy = parseResult.GetValue(cyOpt);
            int radius = parseResult.GetValue(radiusOpt);
            double strength = parseResult.GetValue(strengthOpt);
            double dx = parseResult.GetValue(dxOpt);
            double dy = parseResult.GetValue(dyOpt);
            if (!CliOutput.ValidateInputExists(input)) return;
            CliOutput.RunPipeline(input, output, "Liquify",
                img => AdvancedTransform.Liquify(img, cx, cy, radius, strength, dx, dy));
        });
        return cmd;
    }

    public static Command CreateFreqSepCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image" };
        var lowOutputArg = new Argument<string>("low-output") { Description = "Low frequency output path" };
        var highOutputArg = new Argument<string>("high-output") { Description = "High frequency output path" };
        var radiusOpt = new Option<double>("--radius") { Description = "Blur radius", DefaultValueFactory = _ => 5.0 };
        var cmd = new Command("freqsep", "Decompose image into low/high frequency layers.");
        cmd.Add(inputArg); cmd.Add(lowOutputArg); cmd.Add(highOutputArg); cmd.Add(radiusOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string lowOut = parseResult.GetValue(lowOutputArg)!;
            string highOut = parseResult.GetValue(highOutputArg)!;
            double radius = parseResult.GetValue(radiusOpt);
            if (!CliOutput.ValidateInputExists(input)) return;

            var src = FormatRegistry.Read(input);
            var (low, high) = AdvancedTransform.FrequencySeparation(src, radius);
            CliOutput.WriteImageWithProgress(low, lowOut);
            CliOutput.WriteImageWithProgress(high, highOut);
            src.Dispose(); low.Dispose(); high.Dispose();
        });
        return cmd;
    }

    public static Command CreateFocusStackCommand()
    {
        var outputArg = new Argument<string>("output") { Description = "Output stacked image" };
        var inputsOpt = new Option<string[]>("--inputs") { Description = "Input image paths", Required = true };
        inputsOpt.AllowMultipleArgumentsPerToken = true;
        var cmd = new Command("focusstack", "Combine multiple focal planes into an all-sharp image.");
        cmd.Add(outputArg); cmd.Add(inputsOpt);
        cmd.SetAction((parseResult) =>
        {
            string output = parseResult.GetValue(outputArg)!;
            string[] inputs = parseResult.GetValue(inputsOpt)!;

            foreach (var inp in inputs)
                if (!CliOutput.ValidateInputExists(inp)) return;

            var images = inputs.Select(p => FormatRegistry.Read(p)).ToArray();
            var result = AdvancedTransform.FocusStack(images);
            CliOutput.WriteImageWithProgress(result, output);
            foreach (var img in images) img.Dispose();
            result.Dispose();
        });
        return cmd;
    }

    public static Command CreateThumbnailCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output thumbnail file path" };
        var widthOpt = new Option<int>("--width") { Description = "Maximum width", DefaultValueFactory = _ => 200 };
        var heightOpt = new Option<int>("--height") { Description = "Maximum height", DefaultValueFactory = _ => 200 };
        var cmd = new Command("thumbnail", """
            Create a thumbnail that fits within the specified box while preserving aspect ratio.
            Strips metadata (EXIF, ICC, XMP) from the result.

            Examples:
              sharpimage thumbnail photo.jpg thumb.png --width 150 --height 150
              sharpimage thumbnail scan.tiff thumb.webp --width 300 --height 200
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Options.Add(widthOpt); cmd.Options.Add(heightOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            int maxW = parseResult.GetValue(widthOpt);
            int maxH = parseResult.GetValue(heightOpt);
            CliOutput.RunPipeline(input, output, "Thumbnail", img => Resize.Thumbnail(img, maxW, maxH));
        });
        return cmd;
    }

    public static Command CreateResampleCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var dpiOpt = new Option<double>("--dpi") { Description = "Target DPI (applied to both X and Y)", DefaultValueFactory = _ => 72.0 };
        var cmd = new Command("resample", """
            Resample an image to a new resolution (DPI), resizing proportionally.
            For example, resampling a 300 DPI image to 72 DPI shrinks it to 24%.

            Examples:
              sharpimage resample scan300.png web.png --dpi 72
              sharpimage resample web.png print.png --dpi 300
            """);
        cmd.Add(inputArg); cmd.Add(outputArg); cmd.Options.Add(dpiOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            double dpi = parseResult.GetValue(dpiOpt);
            CliOutput.RunPipeline(input, output, "Resample", img => Resize.Resample(img, dpi, dpi));
        });
        return cmd;
    }

    public static Command CreateMagnifyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("magnify", "Double the image dimensions (2× scale up) using Lanczos3.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Magnify", img => Resize.Magnify(img));
        });
        return cmd;
    }

    public static Command CreateMinifyCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var cmd = new Command("minify", "Halve the image dimensions (2× scale down) using Lanczos3.");
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            CliOutput.RunPipeline(input, output, "Minify", img => Resize.Minify(img));
        });
        return cmd;
    }

    public static Command CreateCropToTilesCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output directory for tiles" };
        var tileWOpt = new Option<int>("--tile-width") { Description = "Tile width in pixels", DefaultValueFactory = _ => 256 };
        var tileHOpt = new Option<int>("--tile-height") { Description = "Tile height in pixels", DefaultValueFactory = _ => 256 };
        var cmd = new Command("croptiles", """
            Split an image into a grid of tiles. Tiles are saved as tile_Y_X.png in the output directory.
            Edge tiles may be smaller if the image doesn't divide evenly.

            Examples:
              sharpimage croptiles photo.png tiles/ --tile-width 256 --tile-height 256
              sharpimage croptiles map.png tiles/ --tile-width 512 --tile-height 512
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Options.Add(tileWOpt); cmd.Options.Add(tileHOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string outputDir = parseResult.GetValue(outputArg)!;
            int tileW = parseResult.GetValue(tileWOpt);
            int tileH = parseResult.GetValue(tileHOpt);

            if (!CliOutput.ValidateInputExists(input)) return;
            Directory.CreateDirectory(outputDir);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var image = CliOutput.ReadImageWithProgress(input);
            var tiles = Geometry.CropToTiles(image, tileW, tileH);

            int cols = ((int)image.Columns + tileW - 1) / tileW;
            for (int i = 0; i < tiles.Count; i++)
            {
                int row = i / cols;
                int col = i % cols;
                string tilePath = Path.Combine(outputDir, $"tile_{row}_{col}.png");
                FormatRegistry.Write(tiles[i], tilePath);
                tiles[i].Dispose();
            }

            sw.Stop();
            image.Dispose();
            CliOutput.PrintSuccess(input, outputDir, sw.Elapsed);
        });
        return cmd;
    }

    public static Command CreateSampleCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };
        var widthOpt = new Option<int>("--width") { Description = "Target width in pixels", Required = true };
        var heightOpt = new Option<int>("--height") { Description = "Target height in pixels", Required = true };

        var cmd = new Command("sample", """
            Non-interpolated point-sample resize. Selects the nearest source pixel without filtering.
            Ideal for pixel art or images where sharp edges must be preserved.

            Examples:
              sharpimage sample sprite.png large.png --width 256 --height 256
              sharpimage sample photo.png small.png --width 100 --height 100
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            CliOutput.RunPipeline(inPath, outPath, "Point-sample resize", img =>
                Resize.Sample(img, w, h));
        });
        return cmd;
    }

    public static Command CreateAdaptiveResizeCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image path" };
        var outputArg = new Argument<string>("output") { Description = "Output image path" };
        var widthOpt = new Option<int>("--width") { Description = "Target width in pixels", Required = true };
        var heightOpt = new Option<int>("--height") { Description = "Target height in pixels", Required = true };

        var cmd = new Command("adaptive-resize", """
            Area-weighted adaptive resize. Averages overlapping source pixels for downscaling,
            providing smoother results than nearest-neighbor.

            Examples:
              sharpimage adaptive-resize photo.png small.png --width 200 --height 150
            """);
        cmd.Add(inputArg);
        cmd.Add(outputArg);
        cmd.Add(widthOpt);
        cmd.Add(heightOpt);

        cmd.SetAction((parseResult) =>
        {
            string inPath = parseResult.GetValue(inputArg)!;
            string outPath = parseResult.GetValue(outputArg)!;
            int w = parseResult.GetValue(widthOpt);
            int h = parseResult.GetValue(heightOpt);
            if (!CliOutput.ValidateInputExists(inPath)) return;

            CliOutput.RunPipeline(inPath, outPath, "Adaptive resize", img =>
                Resize.AdaptiveResize(img, w, h));
        });
        return cmd;
    }

    public static Command CreateAffineCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Input image file path" };
        var outputArg = new Argument<string>("output") { Description = "Output image file path" };
        var sxOpt = new Option<double>("--sx") { Description = "Scale X (default 1.0)", DefaultValueFactory = _ => 1.0 };
        var syOpt = new Option<double>("--sy") { Description = "Scale Y (default 1.0)", DefaultValueFactory = _ => 1.0 };
        var rxOpt = new Option<double>("--rx") { Description = "Shear/rotate X (default 0.0)", DefaultValueFactory = _ => 0.0 };
        var ryOpt = new Option<double>("--ry") { Description = "Shear/rotate Y (default 0.0)", DefaultValueFactory = _ => 0.0 };
        var txOpt = new Option<double>("--tx") { Description = "Translate X in pixels (default 0.0)", DefaultValueFactory = _ => 0.0 };
        var tyOpt = new Option<double>("--ty") { Description = "Translate Y in pixels (default 0.0)", DefaultValueFactory = _ => 0.0 };
        var wOpt = new Option<uint>("-w", "--width") { Description = "Output width (default: same as input)", DefaultValueFactory = _ => 0u };
        var hOpt = new Option<uint>("-h", "--height") { Description = "Output height (default: same as input)", DefaultValueFactory = _ => 0u };
        var cmd = new Command("affine", """
            Apply a 2D affine transformation using a 3x2 matrix.
            The matrix maps each output pixel (x',y') to source coordinates:
              srcX = sx*x' + rx*y' + tx
              srcY = ry*x' + sy*y' + ty

            Examples:
              sharpimage affine photo.jpg --sx 0.5 --sy 0.5 half.png
              sharpimage affine photo.jpg --rx 0.2 --ry -0.2 sheared.png
              sharpimage affine photo.jpg --tx 50 --ty 30 translated.png
            """);
        cmd.Add(inputArg); cmd.Add(outputArg);
        cmd.Add(sxOpt); cmd.Add(syOpt); cmd.Add(rxOpt); cmd.Add(ryOpt);
        cmd.Add(txOpt); cmd.Add(tyOpt); cmd.Add(wOpt); cmd.Add(hOpt);
        cmd.SetAction((parseResult) =>
        {
            string input = parseResult.GetValue(inputArg)!;
            string output = parseResult.GetValue(outputArg)!;
            if (!CliOutput.ValidateInputExists(input)) return;

            uint w = parseResult.GetValue(wOpt);
            uint h = parseResult.GetValue(hOpt);

            var matrix = new SharpImage.Core.AffineMatrix
            {
                Sx = parseResult.GetValue(sxOpt),
                Rx = parseResult.GetValue(rxOpt),
                Ry = parseResult.GetValue(ryOpt),
                Sy = parseResult.GetValue(syOpt),
                Tx = parseResult.GetValue(txOpt),
                Ty = parseResult.GetValue(tyOpt)
            };

            CliOutput.RunPipeline(input, output, "Affine transform", img =>
            {
                uint outW = w > 0 ? w : (uint)img.Columns;
                uint outH = h > 0 ? h : (uint)img.Rows;
                return Distort.Affine(img, matrix, outW, outH);
            });
        });
        return cmd;
    }
}
