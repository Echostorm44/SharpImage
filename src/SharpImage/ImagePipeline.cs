// SharpImage — Fluent pipeline API for chaining image operations.
// Manages intermediate frame disposal automatically.

using SharpImage.Effects;
using SharpImage.Enhance;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;
using CompositeOp = SharpImage.Transform.Composite;
using ResizeOp = SharpImage.Transform.Resize;

namespace SharpImage;

/// <summary>
/// Fluent pipeline for chaining image operations with automatic intermediate disposal.
/// Usage: ImagePipeline.Load("in.png").Resize(800,600).GaussianBlur(1.0).Save("out.png");
/// </summary>
public sealed class ImagePipeline : IDisposable
{
    private ImageFrame? currentFrame;
    private bool disposed;

    private ImagePipeline(ImageFrame frame)
    {
        currentFrame = frame;
    }

    /// <summary>Load an image from disk and start a pipeline.</summary>
    public static ImagePipeline Load(string path)
    {
        return new ImagePipeline(FormatRegistry.Read(path));
    }

    /// <summary>Start a pipeline from an existing frame (clones it to avoid side effects).</summary>
    public static ImagePipeline From(ImageFrame source)
    {
        return new ImagePipeline(source.Clone());
    }

    /// <summary>Start a pipeline from an existing frame without cloning (caller loses ownership).</summary>
    public static ImagePipeline Wrap(ImageFrame source)
    {
        return new ImagePipeline(source);
    }

    private ImageFrame Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return currentFrame ?? throw new InvalidOperationException("Pipeline frame has been extracted.");
        }
    }

    private ImagePipeline Swap(ImageFrame newFrame)
    {
        currentFrame?.Dispose();
        currentFrame = newFrame;
        return this;
    }

    // ── Resize ──────────────────────────────────────────────────────

    public ImagePipeline Resize(int width, int height, InterpolationMethod method = InterpolationMethod.Lanczos3)
        => Swap(ResizeOp.Apply(Frame, width, height, method));

    public ImagePipeline SeamCarve(int targetWidth, int targetHeight)
        => Swap(SeamCarving.Apply(Frame, targetWidth, targetHeight));

    public ImagePipeline SmartCrop(int targetWidth, int targetHeight)
        => Swap(Transform.SmartCrop.Apply(Frame, targetWidth, targetHeight));

    // ── Geometry ────────────────────────────────────────────────────

    public ImagePipeline Crop(int x, int y, int width, int height)
        => Swap(Geometry.Crop(Frame, x, y, width, height));

    public ImagePipeline Flip() => Swap(Geometry.Flip(Frame));

    public ImagePipeline Flop() => Swap(Geometry.Flop(Frame));

    public ImagePipeline Rotate(RotationAngle angle) => Swap(Geometry.Rotate(Frame, angle));

    public ImagePipeline Rotate(double angleDegrees) => Swap(Geometry.RotateArbitrary(Frame, angleDegrees));

    public ImagePipeline Trim(double fuzz = 0.0) => Swap(Geometry.Trim(Frame, fuzz));

    public ImagePipeline Extent(int newWidth, int newHeight, int xOffset = 0, int yOffset = 0,
        ushort bgR = 0, ushort bgG = 0, ushort bgB = 0)
        => Swap(Geometry.Extent(Frame, newWidth, newHeight, xOffset, yOffset, bgR, bgG, bgB));

    public ImagePipeline Shave(int horizontal, int vertical)
        => Swap(Geometry.Shave(Frame, horizontal, vertical));

    public ImagePipeline Chop(int chopX, int chopY, int chopWidth, int chopHeight)
        => Swap(Geometry.Chop(Frame, chopX, chopY, chopWidth, chopHeight));

    public ImagePipeline Transpose() => Swap(Geometry.Transpose(Frame));

    public ImagePipeline Transverse() => Swap(Geometry.Transverse(Frame));

    public ImagePipeline Deskew(double threshold = 0.4) => Swap(Geometry.Deskew(Frame, threshold));

    // ── Composite ───────────────────────────────────────────────────

    public ImagePipeline Composite(ImageFrame overlay, int offsetX = 0, int offsetY = 0,
        CompositeMode mode = CompositeMode.Over)
    {
        CompositeOp.Apply(Frame, overlay, offsetX, offsetY, mode);
        return this;
    }

    // ── Filters ─────────────────────────────────────────────────────

    public ImagePipeline GaussianBlur(double sigma = 1.0)
        => Swap(ConvolutionFilters.GaussianBlur(Frame, sigma));

    public ImagePipeline BoxBlur(int radius = 1) => Swap(ConvolutionFilters.BoxBlur(Frame, radius));

    public ImagePipeline Sharpen(double sigma = 1.0, double amount = 1.0)
        => Swap(ConvolutionFilters.Sharpen(Frame, sigma, amount));

    public ImagePipeline EdgeDetect() => Swap(ConvolutionFilters.EdgeDetect(Frame));

    public ImagePipeline EdgeDetect(double radius) => Swap(ConvolutionFilters.EdgeDetect(Frame, radius));

    public ImagePipeline Emboss() => Swap(ConvolutionFilters.Emboss(Frame));

    // ── Blur & Noise ────────────────────────────────────────────────

    public ImagePipeline MotionBlur(int radius, double sigma, double angle)
        => Swap(BlurNoiseOps.MotionBlur(Frame, radius, sigma, angle));

    public ImagePipeline RadialBlur(double angle)
        => Swap(BlurNoiseOps.RadialBlur(Frame, angle));

    public ImagePipeline SelectiveBlur(int radius, double sigma, double threshold)
        => Swap(BlurNoiseOps.SelectiveBlur(Frame, radius, sigma, threshold));

    public ImagePipeline AddNoise(NoiseType noiseType, double attenuate = 1.0)
        => Swap(BlurNoiseOps.AddNoise(Frame, noiseType, attenuate));

    public ImagePipeline Despeckle() => Swap(BlurNoiseOps.Despeckle(Frame));

    public ImagePipeline WaveletDenoise(double threshold, double softness = 0.0)
        => Swap(BlurNoiseOps.WaveletDenoise(Frame, threshold, softness));

    // ── Color Adjustments ───────────────────────────────────────────

    public ImagePipeline Brightness(double factor) => Swap(ColorAdjust.Brightness(Frame, factor));

    public ImagePipeline Contrast(double factor) => Swap(ColorAdjust.Contrast(Frame, factor));

    public ImagePipeline Gamma(double gamma) => Swap(ColorAdjust.Gamma(Frame, gamma));

    public ImagePipeline Grayscale() => Swap(ColorAdjust.Grayscale(Frame));

    public ImagePipeline Invert() => Swap(ColorAdjust.Invert(Frame));

    public ImagePipeline Threshold(double threshold = 0.5)
        => Swap(ColorAdjust.Threshold(Frame, threshold));

    public ImagePipeline Levels(double inBlack = 0.0, double inWhite = 1.0,
        double outBlack = 0.0, double outWhite = 1.0, double midGamma = 1.0)
        => Swap(ColorAdjust.Levels(Frame, inBlack, inWhite, outBlack, outWhite, midGamma));

    public ImagePipeline Posterize(int levels = 4) => Swap(ColorAdjust.Posterize(Frame, levels));

    public ImagePipeline Saturate(double factor) => Swap(ColorAdjust.Saturate(Frame, factor));

    // ── Enhancement ─────────────────────────────────────────────────

    public ImagePipeline Equalize() => Swap(EnhanceOps.Equalize(Frame));

    public ImagePipeline Normalize() => Swap(EnhanceOps.Normalize(Frame));

    public ImagePipeline AutoLevel() => Swap(EnhanceOps.AutoLevel(Frame));

    public ImagePipeline AutoGamma() => Swap(EnhanceOps.AutoGamma(Frame));

    public ImagePipeline SigmoidalContrast(double contrast, double midpoint = 0.5, bool sharpen = true)
        => Swap(EnhanceOps.SigmoidalContrast(Frame, contrast, midpoint, sharpen));

    public ImagePipeline CLAHE(int tilesX = 8, int tilesY = 8, double clipLimit = 2.0)
        => Swap(EnhanceOps.CLAHE(Frame, tilesX, tilesY, clipLimit));

    public ImagePipeline Modulate(double brightness = 100, double saturation = 100, double hue = 100)
        => Swap(EnhanceOps.Modulate(Frame, brightness, saturation, hue));

    public ImagePipeline WhiteBalance() => Swap(EnhanceOps.WhiteBalance(Frame));

    public ImagePipeline Colorize(double tintR, double tintG, double tintB, double amount = 0.5)
        => Swap(EnhanceOps.Colorize(Frame, tintR, tintG, tintB, amount));

    public ImagePipeline Solarize(double threshold = 0.5)
        => Swap(EnhanceOps.Solarize(Frame, threshold));

    public ImagePipeline SepiaTone(double threshold = 0.8)
        => Swap(EnhanceOps.SepiaTone(Frame, threshold));

    // ── Artistic ────────────────────────────────────────────────────

    public ImagePipeline OilPaint(int radius = 3, int levels = 256)
        => Swap(ArtisticOps.OilPaint(Frame, radius, levels));

    public ImagePipeline Charcoal(double radius = 1.0, double sigma = 0.5)
        => Swap(ArtisticOps.Charcoal(Frame, radius, sigma));

    public ImagePipeline Sketch(double sigma = 0.5, double angle = 45.0)
        => Swap(ArtisticOps.Sketch(Frame, sigma, angle));

    public ImagePipeline Vignette(double sigma = 10.0)
        => Swap(ArtisticOps.Vignette(Frame, sigma));

    public ImagePipeline Wave(double amplitude = 10.0, double wavelength = 50.0)
        => Swap(ArtisticOps.Wave(Frame, amplitude, wavelength));

    public ImagePipeline Swirl(double degrees = 60.0) => Swap(ArtisticOps.Swirl(Frame, degrees));

    public ImagePipeline Implode(double amount = 0.5) => Swap(ArtisticOps.Implode(Frame, amount));

    // ── Histogram ───────────────────────────────────────────────────

    public ImagePipeline HistogramMatch(ImageFrame reference)
        => Swap(HistogramMatching.Apply(Frame, reference));

    // ── Custom Operation ────────────────────────────────────────────

    /// <summary>Apply any custom operation that takes and returns an ImageFrame.</summary>
    public ImagePipeline Apply(Func<ImageFrame, ImageFrame> operation)
        => Swap(operation(Frame));

    /// <summary>Inspect the current frame without modifying it (for debugging/logging).</summary>
    public ImagePipeline Inspect(Action<ImageFrame> action)
    {
        action(Frame);
        return this;
    }

    // ── Terminal Operations ─────────────────────────────────────────

    /// <summary>Save the result to disk. The pipeline remains usable after saving.</summary>
    public ImagePipeline Save(string path)
    {
        FormatRegistry.Write(Frame, path);
        return this;
    }

    /// <summary>Encode the result to a byte array.</summary>
    public byte[] Encode(ImageFileFormat format) => FormatRegistry.Encode(Frame, format);

    /// <summary>Extract the final frame. The pipeline no longer owns it; caller must dispose.</summary>
    public ImageFrame ToFrame()
    {
        var frame = Frame;
        currentFrame = null;
        return frame;
    }

    /// <summary>Get the current image dimensions.</summary>
    public (long Width, long Height) Size => (Frame.Columns, Frame.Rows);

    public void Dispose()
    {
        if (!disposed)
        {
            currentFrame?.Dispose();
            currentFrame = null;
            disposed = true;
        }
    }

    // ── Pipeline Text Parsing (for CLI) ─────────────────────────────

    /// <summary>Parse and execute a series of text-based operation steps.</summary>
    public static ImagePipeline Execute(string inputPath, IReadOnlyList<string> steps)
    {
        var pipeline = Load(inputPath);
        foreach (var step in steps)
            pipeline = ApplyStep(pipeline, step);
        return pipeline;
    }

    private static ImagePipeline ApplyStep(ImagePipeline pipeline, string step)
    {
        var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Empty pipeline step.");

        var op = parts[0].ToLowerInvariant();

        return op switch
        {
            "resize" => pipeline.Resize(ParseInt(parts, 1, "width"), ParseInt(parts, 2, "height")),
            "crop" => pipeline.Crop(ParseInt(parts, 1, "x"), ParseInt(parts, 2, "y"),
                ParseInt(parts, 3, "width"), ParseInt(parts, 4, "height")),
            "flip" => pipeline.Flip(),
            "flop" => pipeline.Flop(),
            "rotate" => pipeline.Rotate(ParseDouble(parts, 1, "angle")),
            "trim" => pipeline.Trim(parts.Length > 1 ? ParseDouble(parts, 1, "fuzz") : 0.0),
            "shave" => pipeline.Shave(ParseInt(parts, 1, "horizontal"), ParseInt(parts, 2, "vertical")),
            "transpose" => pipeline.Transpose(),
            "transverse" => pipeline.Transverse(),
            "deskew" => pipeline.Deskew(),
            "seamcarve" => pipeline.SeamCarve(ParseInt(parts, 1, "width"), ParseInt(parts, 2, "height")),
            "smartcrop" => pipeline.SmartCrop(ParseInt(parts, 1, "width"), ParseInt(parts, 2, "height")),

            "blur" or "gaussianblur" => pipeline.GaussianBlur(parts.Length > 1 ? ParseDouble(parts, 1, "sigma") : 1.0),
            "boxblur" => pipeline.BoxBlur(parts.Length > 1 ? ParseInt(parts, 1, "radius") : 1),
            "sharpen" => pipeline.Sharpen(parts.Length > 1 ? ParseDouble(parts, 1, "sigma") : 1.0),
            "edgedetect" => pipeline.EdgeDetect(),
            "emboss" => pipeline.Emboss(),
            "motionblur" => pipeline.MotionBlur(ParseInt(parts, 1, "radius"), ParseDouble(parts, 2, "sigma"), ParseDouble(parts, 3, "angle")),
            "radialblur" => pipeline.RadialBlur(ParseDouble(parts, 1, "angle")),
            "despeckle" => pipeline.Despeckle(),
            "waveletdenoise" => pipeline.WaveletDenoise(ParseDouble(parts, 1, "threshold")),

            "brightness" => pipeline.Brightness(ParseDouble(parts, 1, "factor")),
            "contrast" => pipeline.Contrast(ParseDouble(parts, 1, "factor")),
            "gamma" => pipeline.Gamma(ParseDouble(parts, 1, "gamma")),
            "grayscale" => pipeline.Grayscale(),
            "invert" => pipeline.Invert(),
            "threshold" => pipeline.Threshold(parts.Length > 1 ? ParseDouble(parts, 1, "threshold") : 0.5),
            "posterize" => pipeline.Posterize(parts.Length > 1 ? ParseInt(parts, 1, "levels") : 4),
            "saturate" => pipeline.Saturate(ParseDouble(parts, 1, "factor")),

            "equalize" => pipeline.Equalize(),
            "normalize" => pipeline.Normalize(),
            "autolevel" => pipeline.AutoLevel(),
            "autogamma" => pipeline.AutoGamma(),
            "whitebalance" => pipeline.WhiteBalance(),
            "sepia" or "sepiatone" => pipeline.SepiaTone(parts.Length > 1 ? ParseDouble(parts, 1, "threshold") : 0.8),
            "solarize" => pipeline.Solarize(parts.Length > 1 ? ParseDouble(parts, 1, "threshold") : 0.5),
            "modulate" => pipeline.Modulate(ParseDouble(parts, 1, "brightness"), ParseDouble(parts, 2, "saturation"),
                parts.Length > 3 ? ParseDouble(parts, 3, "hue") : 100),

            "oilpaint" => pipeline.OilPaint(parts.Length > 1 ? ParseInt(parts, 1, "radius") : 3),
            "charcoal" => pipeline.Charcoal(parts.Length > 1 ? ParseDouble(parts, 1, "radius") : 1.0),
            "sketch" => pipeline.Sketch(),
            "vignette" => pipeline.Vignette(parts.Length > 1 ? ParseDouble(parts, 1, "sigma") : 10.0),
            "wave" => pipeline.Wave(ParseDouble(parts, 1, "amplitude"), ParseDouble(parts, 2, "wavelength")),
            "swirl" => pipeline.Swirl(parts.Length > 1 ? ParseDouble(parts, 1, "degrees") : 60.0),
            "implode" => pipeline.Implode(parts.Length > 1 ? ParseDouble(parts, 1, "amount") : 0.5),

            _ => throw new ArgumentException($"Unknown pipeline operation: '{op}'. Use 'sharpimage pipeline --list' for available operations.")
        };
    }

    private static int ParseInt(string[] parts, int index, string name)
    {
        if (index >= parts.Length)
            throw new ArgumentException($"Missing required argument '{name}' for operation '{parts[0]}'.");
        if (!int.TryParse(parts[index], out int value))
            throw new ArgumentException($"Invalid integer '{parts[index]}' for argument '{name}' in operation '{parts[0]}'.");
        return value;
    }

    private static double ParseDouble(string[] parts, int index, string name)
    {
        if (index >= parts.Length)
            throw new ArgumentException($"Missing required argument '{name}' for operation '{parts[0]}'.");
        if (!double.TryParse(parts[index], System.Globalization.CultureInfo.InvariantCulture, out double value))
            throw new ArgumentException($"Invalid number '{parts[index]}' for argument '{name}' in operation '{parts[0]}'.");
        return value;
    }

    /// <summary>List of all supported pipeline operation names and their arguments.</summary>
    public static readonly IReadOnlyList<(string Name, string Args, string Description)> AvailableOperations =
    [
        // Geometry
        ("resize", "width height", "Resize image (Lanczos3)"),
        ("crop", "x y width height", "Crop rectangular region"),
        ("flip", "", "Flip vertically"),
        ("flop", "", "Mirror horizontally"),
        ("rotate", "angle", "Rotate by angle in degrees"),
        ("trim", "[fuzz]", "Auto-trim uniform borders"),
        ("shave", "horizontal vertical", "Remove strips from edges"),
        ("transpose", "", "Flip + rotate 270°"),
        ("transverse", "", "Flop + rotate 270°"),
        ("deskew", "", "Straighten scanned documents"),
        ("seamcarve", "width height", "Content-aware resize"),
        ("smartcrop", "width height", "Entropy-based smart crop"),

        // Filters
        ("blur", "[sigma]", "Gaussian blur (default σ=1.0)"),
        ("boxblur", "[radius]", "Box blur (default radius=1)"),
        ("sharpen", "[sigma]", "Sharpen (default σ=1.0)"),
        ("edgedetect", "", "Edge detection"),
        ("emboss", "", "Emboss effect"),
        ("motionblur", "radius sigma angle", "Directional motion blur"),
        ("radialblur", "angle", "Rotational blur"),
        ("despeckle", "", "Median-based noise reduction"),
        ("waveletdenoise", "threshold", "Multi-scale denoising"),

        // Color
        ("brightness", "factor", "Adjust brightness (1.0=original)"),
        ("contrast", "factor", "Adjust contrast (1.0=original)"),
        ("gamma", "gamma", "Gamma correction"),
        ("grayscale", "", "Convert to grayscale"),
        ("invert", "", "Invert colors"),
        ("threshold", "[threshold]", "Binary threshold (default 0.5)"),
        ("posterize", "[levels]", "Posterize (default 4 levels)"),
        ("saturate", "factor", "Adjust saturation"),

        // Enhancement
        ("equalize", "", "Histogram equalization"),
        ("normalize", "", "Stretch to full range"),
        ("autolevel", "", "Per-channel auto levels"),
        ("autogamma", "", "Automatic gamma correction"),
        ("whitebalance", "", "Automatic white balance"),
        ("sepia", "[threshold]", "Sepia tone (default 0.8)"),
        ("solarize", "[threshold]", "Solarize (default 0.5)"),
        ("modulate", "brightness saturation [hue]", "HSB modulation"),

        // Artistic
        ("oilpaint", "[radius]", "Oil painting effect"),
        ("charcoal", "[radius]", "Charcoal sketch"),
        ("sketch", "", "Pencil sketch"),
        ("vignette", "[sigma]", "Vignette darkening"),
        ("wave", "amplitude wavelength", "Sine wave distortion"),
        ("swirl", "[degrees]", "Rotational swirl"),
        ("implode", "[amount]", "Spherical implode/explode"),
    ];
}
