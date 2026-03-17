using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using SharpImage.Analysis;
using SharpImage.Colorspaces;
using SharpImage.Core;
using SharpImage.Effects;
using SharpImage.Formats;
using SharpImage.Fourier;
using SharpImage.Image;
using SharpImage.Transform;

BenchmarkSwitcher.FromAssembly(typeof(ResizeBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class ResizeBenchmarks
{
    private ImageFrame small = null!;  // 256x256
    private ImageFrame medium = null!; // 1024x1024

    [GlobalSetup]
    public void Setup()
    {
        small = CreateGradientImage(256, 256);
        medium = CreateGradientImage(1024, 1024);
    }

    [Benchmark(Description = "Resize 256→128 Nearest")]
    public ImageFrame Resize_Small_Nearest() =>
        Resize.Apply(small, 128, 128, InterpolationMethod.NearestNeighbor);

    [Benchmark(Description = "Resize 256→128 Bilinear")]
    public ImageFrame Resize_Small_Bilinear() =>
        Resize.Apply(small, 128, 128, InterpolationMethod.Bilinear);

    [Benchmark(Description = "Resize 256→128 Bicubic")]
    public ImageFrame Resize_Small_Bicubic() =>
        Resize.Apply(small, 128, 128, InterpolationMethod.Bicubic);

    [Benchmark(Description = "Resize 256→128 Lanczos3")]
    public ImageFrame Resize_Small_Lanczos3() =>
        Resize.Apply(small, 128, 128, InterpolationMethod.Lanczos3);

    [Benchmark(Description = "Resize 1024→512 Lanczos3")]
    public ImageFrame Resize_Medium_Lanczos3() =>
        Resize.Apply(medium, 512, 512, InterpolationMethod.Lanczos3);

    [Benchmark(Description = "Resize 1024→2048 Lanczos3 (upscale)")]
    public ImageFrame Resize_Medium_Upscale() =>
        Resize.Apply(medium, 2048, 2048, InterpolationMethod.Lanczos3);

    public static ImageFrame CreateGradientImage(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0;y < h;y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * 3;
                row[offset] = (ushort)(x * 65535 / w);
                row[offset + 1] = (ushort)(y * 65535 / h);
                row[offset + 2] = (ushort)((x + y) * 65535 / (w + h));
            }
        }
        return img;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class ConvolutionBenchmarks
{
    private ImageFrame small = null!;
    private ImageFrame medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = ResizeBenchmarks.CreateGradientImage(256, 256);
        medium = ResizeBenchmarks.CreateGradientImage(512, 512);
    }

    [Benchmark(Description = "GaussianBlur σ=1.0 256x256")]
    public ImageFrame Blur_Small() => ConvolutionFilters.GaussianBlur(small, 1.0);

    [Benchmark(Description = "GaussianBlur σ=3.0 256x256")]
    public ImageFrame Blur_Small_Large() => ConvolutionFilters.GaussianBlur(small, 3.0);

    [Benchmark(Description = "GaussianBlur σ=1.0 512x512")]
    public ImageFrame Blur_Medium() => ConvolutionFilters.GaussianBlur(medium, 1.0);

    [Benchmark(Description = "Sharpen 256x256")]
    public ImageFrame Sharpen_Small() => ConvolutionFilters.Sharpen(small);

    [Benchmark(Description = "EdgeDetect 256x256")]
    public ImageFrame Edge_Small() => ConvolutionFilters.EdgeDetect(small);
}

[MemoryDiagnoser]
[ShortRunJob]
public class ColorspaceBenchmarks
{
    private const int Iterations = 100_000;

    [Benchmark(Description = "RGB→Lab 100K pixels")]
    public void RgbToLab()
    {
        for (int i = 0;i < Iterations;i++)
        {
            double r = i / (double)Iterations;
            ColorspaceConverter.RgbToLab(r, 0.5, 0.3, out _, out _, out _);
        }
    }

    [Benchmark(Description = "Lab→RGB 100K pixels")]
    public void LabToRgb()
    {
        for (int i = 0;i < Iterations;i++)
        {
            double l = i * 100.0 / Iterations;
            ColorspaceConverter.LabToRgb(l, 20.0, -30.0, out _, out _, out _);
        }
    }

    [Benchmark(Description = "RGB→HSL 100K pixels")]
    public void RgbToHsl()
    {
        for (int i = 0;i < Iterations;i++)
        {
            double r = i / (double)Iterations;
            ColorspaceConverter.RgbToHsl(r, 0.5, 0.3, out _, out _, out _);
        }
    }

    [Benchmark(Description = "RGB→XYZ 100K pixels")]
    public void RgbToXyz()
    {
        for (int i = 0;i < Iterations;i++)
        {
            double r = i / (double)Iterations;
            ColorspaceConverter.RgbToXyz(r, 0.5, 0.3, out _, out _, out _);
        }
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class ComparisonBenchmarks
{
    private ImageFrame imageA = null!;
    private ImageFrame imageB = null!;

    [GlobalSetup]
    public void Setup()
    {
        imageA = ResizeBenchmarks.CreateGradientImage(256, 256);
        imageB = ConvolutionFilters.GaussianBlur(imageA, 1.0);
    }

    [Benchmark(Description = "MSE 256x256")]
    public double MSE() => ImageCompare.MeanSquaredError(imageA, imageB);

    [Benchmark(Description = "SSIM 256x256")]
    public double SSIM() => ImageCompare.StructuralSimilarity(imageA, imageB);
}

[MemoryDiagnoser]
[ShortRunJob]
public class FourierBenchmarks
{
    private ImageFrame small = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = ResizeBenchmarks.CreateGradientImage(128, 128);
    }

    [Benchmark(Description = "FFT Forward 128x128")]
    public (double[], double[]) FFT_128() => FourierTransform.Forward(small);
}

[MemoryDiagnoser]
[ShortRunJob]
public class FormatBenchmarks
{
    private ImageFrame testImage = null!;
    private byte[] pngBytes = null!;
    private byte[] jpegBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        testImage = ResizeBenchmarks.CreateGradientImage(512, 512);

        using var pngStream = new MemoryStream();
        PngCoder.Write(testImage, pngStream);
        pngBytes = pngStream.ToArray();

        using var jpegStream = new MemoryStream();
        JpegCoder.Write(testImage, jpegStream, 85);
        jpegBytes = jpegStream.ToArray();
    }

    [Benchmark(Description = "PNG Encode 512x512")]
    public void PngEncode()
    {
        using var ms = new MemoryStream();
        PngCoder.Write(testImage, ms);
    }

    [Benchmark(Description = "PNG Decode 512x512")]
    public ImageFrame PngDecode() => PngCoder.Read(new MemoryStream(pngBytes));

    [Benchmark(Description = "JPEG Encode 512x512 Q85")]
    public void JpegEncode()
    {
        using var ms = new MemoryStream();
        JpegCoder.Write(testImage, ms, 85);
    }

    [Benchmark(Description = "JPEG Decode 512x512")]
    public ImageFrame JpegDecode() => JpegCoder.Read(new MemoryStream(jpegBytes));
}

// Helper to make CreateGradientImage accessible from other benchmark classes
public static class BenchmarkHelpers
{
    public static ImageFrame CreateGradientImage(int w, int h)
    {
        var img = new ImageFrame();
        img.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0;y < h;y++)
        {
            var row = img.GetPixelRowForWrite(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * 3;
                row[offset] = (ushort)(x * 65535 / w);
                row[offset + 1] = (ushort)(y * 65535 / h);
                row[offset + 2] = (ushort)((x + y) * 65535 / (w + h));
            }
        }
        return img;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class EnhanceBenchmarks
{
    private ImageFrame small = null!;
    private ImageFrame medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = BenchmarkHelpers.CreateGradientImage(256, 256);
        medium = BenchmarkHelpers.CreateGradientImage(512, 512);
    }

    [Benchmark(Description = "Equalize 256x256")]
    public ImageFrame Equalize_Small() => SharpImage.Enhance.EnhanceOps.Equalize(small);

    [Benchmark(Description = "Equalize 512x512")]
    public ImageFrame Equalize_Medium() => SharpImage.Enhance.EnhanceOps.Equalize(medium);

    [Benchmark(Description = "Normalize 256x256")]
    public ImageFrame Normalize_Small() => SharpImage.Enhance.EnhanceOps.Normalize(small);

    [Benchmark(Description = "Normalize 512x512")]
    public ImageFrame Normalize_Medium() => SharpImage.Enhance.EnhanceOps.Normalize(medium);

    [Benchmark(Description = "AutoLevel 256x256")]
    public ImageFrame AutoLevel_Small() => SharpImage.Enhance.EnhanceOps.AutoLevel(small);

    [Benchmark(Description = "CLAHE 256x256")]
    public ImageFrame CLAHE_Small() => SharpImage.Enhance.EnhanceOps.CLAHE(small);
}

[MemoryDiagnoser]
[ShortRunJob]
public class BlurNoiseBenchmarks
{
    private ImageFrame small = null!;
    private ImageFrame medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = BenchmarkHelpers.CreateGradientImage(256, 256);
        medium = BenchmarkHelpers.CreateGradientImage(512, 512);
    }

    [Benchmark(Description = "MotionBlur r=5 256x256")]
    public ImageFrame MotionBlur_Small() => BlurNoiseOps.MotionBlur(small, 5, 1.5, 45.0);

    [Benchmark(Description = "MotionBlur r=5 512x512")]
    public ImageFrame MotionBlur_Medium() => BlurNoiseOps.MotionBlur(medium, 5, 1.5, 45.0);

    [Benchmark(Description = "RadialBlur 2° 256x256")]
    public ImageFrame RadialBlur_Small() => BlurNoiseOps.RadialBlur(small, 2.0);

    [Benchmark(Description = "SelectiveBlur 256x256")]
    public ImageFrame SelectiveBlur_Small() => BlurNoiseOps.SelectiveBlur(small, 2, 1.0, 0.2);

    [Benchmark(Description = "AddNoise Gaussian 256x256")]
    public ImageFrame AddNoise_Small() => BlurNoiseOps.AddNoise(small, NoiseType.Gaussian);

    [Benchmark(Description = "WaveletDenoise 256x256")]
    public ImageFrame WaveletDenoise_Small() => BlurNoiseOps.WaveletDenoise(small, 0.05);
}

[MemoryDiagnoser]
[ShortRunJob]
public class ArtisticBenchmarks
{
    private ImageFrame small = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = BenchmarkHelpers.CreateGradientImage(256, 256);
    }

    [Benchmark(Description = "OilPaint r=3 256x256")]
    public ImageFrame OilPaint_Small() => ArtisticOps.OilPaint(small, 3);

    [Benchmark(Description = "Charcoal 256x256")]
    public ImageFrame Charcoal_Small() => ArtisticOps.Charcoal(small);

    [Benchmark(Description = "Swirl 45° 256x256")]
    public ImageFrame Swirl_Small() => ArtisticOps.Swirl(small, 45.0);

    [Benchmark(Description = "Wave 256x256")]
    public ImageFrame Wave_Small() => ArtisticOps.Wave(small, 10.0, 50.0);

    [Benchmark(Description = "Vignette 256x256")]
    public ImageFrame Vignette_Small() => ArtisticOps.Vignette(small);
}

[MemoryDiagnoser]
[ShortRunJob]
public class CompositeBenchmarks
{
    private ImageFrame baseImg = null!;
    private ImageFrame overlay = null!;

    [GlobalSetup]
    public void Setup()
    {
        baseImg = BenchmarkHelpers.CreateGradientImage(512, 512);
        overlay = BenchmarkHelpers.CreateGradientImage(256, 256);
    }

    [Benchmark(Description = "Composite Over 512+256")]
    public void Composite_Over()
    {
        var copy = CloneImage(baseImg);
        Composite.Apply(copy, overlay, 128, 128, CompositeMode.Over);
    }

    [Benchmark(Description = "Composite Multiply 512+256")]
    public void Composite_Multiply()
    {
        var copy = CloneImage(baseImg);
        Composite.Apply(copy, overlay, 128, 128, CompositeMode.Multiply);
    }

    [Benchmark(Description = "Composite Screen 512+256")]
    public void Composite_Screen()
    {
        var copy = CloneImage(baseImg);
        Composite.Apply(copy, overlay, 128, 128, CompositeMode.Screen);
    }

    private static ImageFrame CloneImage(ImageFrame src)
    {
        int w = (int)src.Columns, h = (int)src.Rows;
        var clone = new ImageFrame();
        clone.Initialize(w, h, src.Colorspace, src.HasAlpha);
        for (int y = 0;y < h;y++)
        {
            src.GetPixelRow(y).CopyTo(clone.GetPixelRowForWrite(y));
        }

        return clone;
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class AnalysisBenchmarks
{
    private ImageFrame small = null!;
    private ImageFrame medium = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = BenchmarkHelpers.CreateGradientImage(128, 128);
        medium = BenchmarkHelpers.CreateGradientImage(256, 256);
    }

    [Benchmark(Description = "CannyEdge 128x128")]
    public ImageFrame Canny_Small() => CannyEdge.Detect(small);

    [Benchmark(Description = "CannyEdge 256x256")]
    public ImageFrame Canny_Medium() => CannyEdge.Detect(medium);

    [Benchmark(Description = "PerceptualHash 128x128")]
    public ulong PHash_Small() => PerceptualHash.Compute(small);

    [Benchmark(Description = "MeanShift 64x64 r=5")]
    public ImageFrame MeanShift_Tiny()
    {
        var tiny = BenchmarkHelpers.CreateGradientImage(64, 64);
        return MeanShift.Segment(tiny, 5, 0.15, 5);
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class TransformNewBenchmarks
{
    private ImageFrame small = null!;

    [GlobalSetup]
    public void Setup()
    {
        small = BenchmarkHelpers.CreateGradientImage(256, 256);
    }

    [Benchmark(Description = "SeamCarving 256→200 width")]
    public ImageFrame SeamCarve_Small() => SeamCarving.Apply(small, 200, 256);

    [Benchmark(Description = "SmartCrop 256→128x128")]
    public ImageFrame SmartCrop_Small() => SmartCrop.Apply(small, 128, 128);
}

[MemoryDiagnoser]
[ShortRunJob]
public class CameraRawDecodeBenchmarks
{
    private ushort[] syntheticSensor = null!;
    private RawSensorData rawData;

    [GlobalSetup]
    public void Setup()
    {
        // Create a 1024×1024 synthetic Bayer sensor (RGGB)
        int w = 1024, h = 1024;
        syntheticSensor = new ushort[w * h];
        var rng = new Random(42);
        for (int i = 0; i < syntheticSensor.Length; i++)
            syntheticSensor[i] = (ushort)rng.Next(0, 65536);

        rawData = new RawSensorData
        {
            RawPixels = syntheticSensor,
            Width = w,
            Height = h,
            Metadata = new CameraRawMetadata
            {
                BayerPattern = BayerPattern.RGGB,
                CfaType = CfaType.Bayer,
                BitsPerSample = 16,
                WhiteLevel = 65535,
                BlackLevel = 0
            }
        };
    }

    [Benchmark(Description = "Demosaic Bilinear 1024x1024")]
    public ImageFrame Demosaic_Bilinear() =>
        BayerDemosaic.Demosaic(in rawData, DemosaicAlgorithm.Bilinear);

    [Benchmark(Description = "Demosaic VNG 1024x1024")]
    public ImageFrame Demosaic_VNG() =>
        BayerDemosaic.Demosaic(in rawData, DemosaicAlgorithm.VNG);

    [Benchmark(Description = "Demosaic AHD 1024x1024")]
    public ImageFrame Demosaic_AHD() =>
        BayerDemosaic.Demosaic(in rawData, DemosaicAlgorithm.AHD);

    [Benchmark(Description = "ScaleToFullRange 1Mpx")]
    public void ScaleToFullRange()
    {
        // Clone to avoid modifying benchmark data
        var clone = new RawSensorData
        {
            RawPixels = (ushort[])syntheticSensor.Clone(),
            Width = rawData.Width,
            Height = rawData.Height,
            Metadata = rawData.Metadata
        };
        CameraRawProcessor.ScaleToFullRange(ref clone);
    }

    [Benchmark(Description = "Full Pipeline Bilinear 1024x1024")]
    public ImageFrame FullPipeline_Bilinear()
    {
        var clone = new RawSensorData
        {
            RawPixels = (ushort[])syntheticSensor.Clone(),
            Width = rawData.Width,
            Height = rawData.Height,
            Metadata = rawData.Metadata
        };
        CameraRawProcessor.ScaleToFullRange(ref clone);
        var frame = BayerDemosaic.Demosaic(in clone, DemosaicAlgorithm.Bilinear);
        var opts = CameraRawDecodeOptions.Default;
        CameraRawProcessor.Process(frame, in clone.Metadata, in opts);
        return frame;
    }
}
