# SharpImage

A pure C# .NET 10 port of ImageMagick. Zero native dependencies. Full AOT compatibility. No P/Invoke, no C/C++ bindings — just managed code competing at native speed.

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
![NuGet Version](https://img.shields.io/nuget/v/Echostorm.SharpImage)

## Why SharpImage?

ImageMagick is the gold standard for image processing, but it's written in C — a language that has allowed memory corruption, buffer overflows, and CVEs to accumulate for decades. .NET 10 has matured to the point where C# can compete with C and C++ in raw performance when used with care: `Span<T>`, hardware intrinsics (SSE2/SSE4.1/AVX2/AVX-512), `stackalloc`, `ArrayPool<T>`, and aggressive JIT inlining.

SharpImage reimplements ImageMagick's image processing pipeline entirely in safe, managed C# and with better performance — producing a library DLL, a CLI tool, and a GUI Photoshop style example project.

## Features

### 33 Image Formats

| Category | Formats |
|----------|---------|
| **Common Raster** | PNG, JPEG, GIF, BMP, TGA, TIFF, WebP, ICO |
| **Modern Codecs** | AVIF, HEIC, JPEG 2000, JPEG XL, OpenEXR |
| **Professional** | PSD, DPX, Cineon, HDR (Radiance), FITS, DICOM |
| **Compact** | QOI, Farbfeld, WBMP, PNM (PBM/PGM/PPM), PCX |
| **Legacy/Niche** | XBM, XPM, DDS, SGI, PIX, SUN |
| **Vector/Document** | SVG (rasterized), PDF |

### 40+ Colorspaces

sRGB, Linear RGB, scRGB, Adobe98, ProPhoto, Display P3, Lab, LCHab, LCHuv, Luv, OkLab, OkLCH, Jzazbz, XYZ, xyY, LMS, CAT02 LMS, HSL, HSV, HSB, HSI, HCL, HCLp, HWB, CMY, CMYK, YCbCr (Rec.601 & Rec.709), YCC, YDbDr, YIQ, YPbPr, YUV, OHTA, Gray, Linear Gray, Log, Transparent

### 23 Resize Methods

Nearest Neighbor · Bilinear · Bicubic · Lanczos2/3/4/5 · Mitchell · Catrom · Hermite · Triangle · Gaussian · Spline · Sinc · Hann · Hamming · Blackman · Kaiser · Parzen · Bohman · Welch · Bartlett · Lagrange

### 29 Composite Blend Modes

Over · Multiply · Screen · Overlay · Darken · Lighten · ColorDodge · ColorBurn · HardLight · SoftLight · Difference · Exclusion · Add · Subtract · Plus · Minus · Dissolve · Bumpmap · Atop · In · Out · Xor · HardMix · VividLight · LinearLight · PinLight · ModulusAdd · ModulusSubtract · Replace

### 170+ Effects & Operations

| Category | Examples |
|----------|----------|
| **Blur & Noise** | Gaussian, Motion, Radial, Selective, Bilateral, Kuwahara, Wavelet Denoise, Despeckle, Spread |
| **Artistic** | Oil Paint, Charcoal, Sketch, Pencil, Vignette, Wave, Swirl, Implode, Emboss |
| **Color Grading** | Color Transfer, Split Toning, Gradient Map, Channel Mixer, Photo Filter, Duotone |
| **Creative Filters** | Lens Blur, Tilt-Shift, Glow, Pixelate, Crystallize, Pointillize, Halftone |
| **Enhancement** | Equalize, Normalize, AutoLevel, AutoGamma, CLAHE, Sigmoidal Contrast, White Balance, Curves |
| **Retouching** | Inpaint, Clone Stamp, Healing Brush, Red Eye Removal |
| **Selection** | Flood Select, Color Select, GrabCut, Feather, Alpha Matting |
| **HDR** | HDR Merge, Reinhard/Drago Tonemapping, Exposure Fusion |
| **Analysis** | Canny Edge, Hough Lines, Perceptual Hash, Connected Components, Mean Shift Segmentation |
| **Transform** | Resize, Crop, Rotate, Flip, Flop, Transpose, Distort, Deskew, Trim, Seam Carving, Smart Crop, Affine, Liquify |
| **Game Dev** | Sprite Sheet Generation, Cubemap Extract/Stitch |
| **Decoration** | Border, Frame, Raise, Shade, Shear |
| **Morphology** | Erode, Dilate, Open, Close, TopHat, BottomHat, Hit-and-Miss, Skeleton, Thinning |
| **Fourier** | FFT Forward/Inverse |

## Performance

Benchmarks on .NET 10.0.3 with AVX-512, ShortRun (BenchmarkDotNet):

| Operation | Input Size | Time | Memory |
|-----------|-----------|------|--------|
| Resize Nearest | 256→128 | 46 µs | 116 KB |
| Resize Lanczos3 | 256→128 | 236 µs | 110 KB |
| **Resize Lanczos3** | **1024→512** | **1,328 µs** | **106 KB** |
| Resize Lanczos3 (upscale) | 1024→2048 | 4,616 µs | 298 KB |
| GaussianBlur σ=1.0 | 256×256 | 487 µs | 266 KB |
| GaussianBlur σ=1.0 | 512×512 | 1,403 µs | 10 KB |
| Sharpen | 256×256 | 524 µs | 397 KB |
| MotionBlur r=5 | 512×512 | 2,001 µs | 6 KB |
| Equalize | 256×256 | 245 µs | 396 KB |
| Normalize | 256×256 | 124 µs | 387 KB |
| PNG Encode | 512×512 | 7.2 ms | 37 KB |
| PNG Decode | 512×512 | 1.0 ms | 777 KB |
| JPEG Encode Q85 | 512×512 | 10.0 ms | 73 KB |
| JPEG Decode | 512×512 | 9.8 ms | 3,462 KB |
| MSE Compare | 256×256 | 20 µs | 0 B |
| SSIM Compare | 256×256 | 296 µs | 0 B |
| RGB→Lab (100K pixels) | per-pixel | 21 µs | 0 B |
| Composite Over | 512+256 | 238 µs | 1,536 KB |
| CannyEdge | 256×256 | 1,204 µs | 1,880 KB |
| SeamCarving | 256→200 | 10.2 ms | 440 KB |

### Design Principles

- **Zero-allocation hot paths** — `Span<T>`, `stackalloc`, and `ArrayPool<T>` keep the GC out of per-pixel work
- **SIMD everywhere** — Resize, blur, compositing, and pixel conversion use `Vector256`/`Vector128` with FMA where available
- **Branchless arithmetic** — Critical inner loops avoid conditional branching
- **Bounds-check elimination** — Span iteration patterns that let the JIT remove safety checks
- **Cache-friendly layout** — Row-major pixel storage with struct-based pixel types

## Getting Started

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later

### Install via NuGet

```bash
dotnet add package Echostorm.SharpImage
```

Or via the Package Manager Console:

```powershell
Install-Package Echostorm.SharpImage
```

### Build from Source

```bash
git clone https://github.com/Echostorm44/SharpImage.git
cd SharpImage
dotnet build -c Release
```

### Run TUnit Tests

The test suite uses [TUnit](https://github.com/thomhurst/TUnit) with 1,400+ tests covering formats, colorspaces, transforms, effects, compositing, and edge cases.

```bash
# Run the full test suite
dotnet run --project tests/SharpImage.Tests -c Release

# Run tests matching a filter
dotnet run --project tests/SharpImage.Tests -c Release -- --filter "*Png*"
```

### Run BigTest

BigTest is an integration harness that invokes the SharpImage CLI and compares output against ImageMagick. It requires a published AOT build of the CLI first.

```bash
# 1. Publish the CLI (AOT)
dotnet publish src/SharpImage.Cli -c Release

# 2. Run BigTest
dotnet run --project tests/SharpImage.BigTest -c Release
```

### Run Benchmarks

Performance benchmarks use [BenchmarkDotNet](https://benchmarkdotnet.org/). **Always run in Release mode** — Debug builds produce meaningless numbers.

```bash
# Run all benchmarks
dotnet run --project benchmarks/SharpImage.Benchmarks -c Release

# Run a specific benchmark class or filter
dotnet run --project benchmarks/SharpImage.Benchmarks -c Release -- --filter *Resize*

# List available benchmarks without running them
dotnet run --project benchmarks/SharpImage.Benchmarks -c Release -- --list flat
```

> **Tip:** Close other applications during benchmark runs for stable results. BenchmarkDotNet will report mean, median, and allocation statistics automatically.

### CLI Usage

```bash
# Convert between formats
dotnet run --project src/SharpImage.Cli -- convert photo.jpg photo.png

# Resize an image
dotnet run --project src/SharpImage.Cli -- resize photo.jpg -w 800 -h 600 resized.png

# Apply Gaussian blur
dotnet run --project src/SharpImage.Cli -- blur photo.jpg --sigma 3.0 blurred.png

# Composite two images
dotnet run --project src/SharpImage.Cli -- composite background.png overlay.png --mode multiply result.png

# Compare two images
dotnet run --project src/SharpImage.Cli -- compare original.png modified.png --diff difference.png

# Sharpen
dotnet run --project src/SharpImage.Cli -- sharpen photo.jpg --sigma 1.0 --amount 2.0 sharp.png

# See all commands
dotnet run --project src/SharpImage.Cli -- --help
```

Or publish AOT and use directly:

```bash
dotnet publish src/SharpImage.Cli -c Release
# Then use the native binary:
sharpimage convert photo.jpg photo.png
```

### Use as a Library

Add a project reference to `src/SharpImage/SharpImage.csproj`:

```csharp
using SharpImage.Image;
using SharpImage.Transform;
using SharpImage.Formats;

// Load an image
using var stream = File.OpenRead("photo.jpg");
var frame = JpegCoder.Read(stream);

// Resize with Lanczos3
var resized = Resize.Apply(frame, 800, 600, InterpolationMethod.Lanczos3);

// Save as PNG
using var output = File.Create("resized.png");
PngCoder.Write(resized, output);
```

## Architecture

```
SharpImage/
├── src/
│   ├── SharpImage/                 # Core library (DLL)
│   │   ├── Core/                   # Quantum, PixelTypes, Geometry, MemoryPool
│   │   ├── Image/                  # ImageFrame, PixelCache, CacheView, ImageList
│   │   ├── Colorspaces/            # 40+ colorspace converters
│   │   ├── Formats/                # 33 format coders (read/write)
│   │   ├── Transform/              # Resize, Crop, Rotate, Distort, SeamCarving
│   │   ├── Effects/                # 19 effect categories, 170+ operations
│   │   ├── Enhance/                # Equalize, Normalize, CLAHE, Curves, etc.
│   │   ├── Composite/              # 29 blend modes
│   │   ├── Analysis/               # Edge detection, hashing, segmentation
│   │   ├── Fourier/                # FFT forward/inverse
│   │   ├── Draw/                   # Primitives, text, paths
│   │   ├── Morphology/             # Erode, Dilate, Skeleton, Hit-and-Miss
│   │   └── Channel/                # Channel operations, separation, combining
│   └── SharpImage.Cli/             # Command-line interface (228 commands)
├── tests/
│   └── SharpImage.Tests/           # 1,400+ tests (TUnit framework)
└── benchmarks/
    └── SharpImage.Benchmarks/      # BenchmarkDotNet performance suite
```

### Key Types

| Type | Description |
|------|-------------|
| `ImageFrame` | Core image container. Holds pixel data, dimensions, colorspace, alpha channel info |
| `Quantum` | 16-bit (`ushort`) quantum type. `MaxValue = 65535`, `Scale = 1.0 / 65535` |
| `PixelCache` | Memory-mapped pixel storage with disk fallback for large images |
| `FormatRegistry` | Auto-detects and routes to the correct format coder |

### Pixel Storage

- **16-bit quantum** (ushort per channel) — matches ImageMagick's high-quality internal representation
- **Row-major layout** — pixels stored in contiguous rows for cache efficiency
- **3 or 4 channels** — RGB or RGBA, interleaved per pixel
- **Large image support** — automatic disk-backed pixel cache for images exceeding memory limits

## Testing

The test suite uses [TUnit](https://github.com/thomhurst/TUnit) and covers:

- **Format round-trips** — encode→decode→compare for all 33 formats
- **Colorspace accuracy** — forward→inverse conversion for all 40+ colorspaces
- **Transform correctness** — pixel-perfect verification of flip, flop, rotate, crop, resize (all 23 methods)
- **Edge cases** — 1×1 images, 16384×1 strips, all-black/white, fully transparent, extreme resize ratios, odd/prime dimensions, 16-bit quantum extremes
- **Effect coverage** — each effect tested with multiple parameter combinations
- **Composite math** — blend mode output verified against manual calculations

## Contributing

Contributions are welcome. Please:

1. Fork the repository
2. Create a feature branch
3. Write tests for new functionality
4. Ensure all existing tests pass: `dotnet run --project tests/SharpImage.Tests -c Release`
5. Run benchmarks to check for regressions: `dotnet run --project benchmarks/SharpImage.Benchmarks -c Release -- --filter *YourArea*`
6. Submit a pull request

### Code Style

- **PascalCase** for public members, types, methods, properties
- **camelCase** for local variables and private fields
- Prefer `struct` over `class` for small data types
- Use `Span<T>` and `stackalloc` in hot paths
- No dependency injection — direct static method calls
- Keep files focused but don't create separate files for small DTOs

## License

Licensed under the [Apache License 2.0](LICENSE).

## Acknowledgments

- [ImageMagick](https://imagemagick.org/) — the project that defined image processing for decades. SharpImage is a clean-room reimplementation inspired by ImageMagick's feature set and algorithms.
- [TUnit](https://github.com/thomhurst/TUnit) — modern .NET test framework
- [BenchmarkDotNet](https://benchmarkdotnet.org/) — precise .NET benchmarking
- [Spectre.Console](https://spectreconsole.net/) — beautiful CLI output
