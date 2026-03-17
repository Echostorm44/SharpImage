// Unified format detection and I/O registry.
// Provides automatic format detection from file headers and extensions,
// and a single API to read/write any supported format.

using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>
/// Identifies an image file format.
/// </summary>
public enum ImageFileFormat
{
    Unknown,
    Bmp,
    Tga,
    Pnm,
    Gif,
    Jpeg,
    Png,
    Tiff,
    WebP,
    Qoi,
    Ico,
    Hdr,
    Psd,
    Dds,
    Svg,
    Farbfeld,
    Wbmp,
    Pcx,
    Xbm,
    Xpm,
    Dpx,
    Fits,
    Cin,
    Raw,
    Dicom,
    Jpeg2000,
    JpegXl,
    Avif,
    Heic,
    Exr,
    Sgi,
    Pix,
    Sun,
    Pdf,
    CameraRaw
}

/// <summary>
/// Central registry for image format detection and I/O operations. Detects format from file header bytes or file
/// extension.
/// </summary>
public static class FormatRegistry
{
    /// <summary>
    /// Detect image format from the first bytes of file data.
    /// </summary>
    public static ImageFileFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return ImageFileFormat.Unknown;
        }

        // Check signatures in order of specificity
        if (PngCoder.CanDecode(data))
        {
            return ImageFileFormat.Png;
        }

        if (JpegCoder.CanDecode(data))
        {
            return ImageFileFormat.Jpeg;
        }

        if (GifCoder.CanDecode(data))
        {
            return ImageFileFormat.Gif;
        }

        if (WebpCoder.CanDecode(data))
        {
            return ImageFileFormat.WebP;
        }

        // Camera raw formats MUST be checked before TIFF — many raw formats are TIFF-based
        if (CameraRawCoder.CanDecode(data))
        {
            return ImageFileFormat.CameraRaw;
        }

        if (TiffCoder.CanDecode(data))
        {
            return ImageFileFormat.Tiff;
        }

        if (BmpCoder.CanDecode(data))
        {
            return ImageFileFormat.Bmp;
        }

        if (PsdCoder.CanDecode(data))
        {
            return ImageFileFormat.Psd;
        }

        if (DdsCoder.CanDecode(data))
        {
            return ImageFileFormat.Dds;
        }

        if (QoiCoder.CanDecode(data))
        {
            return ImageFileFormat.Qoi;
        }

        if (HdrCoder.CanDecode(data))
        {
            return ImageFileFormat.Hdr;
        }

        if (IcoCoder.CanDecode(data))
        {
            return ImageFileFormat.Ico;
        }

        if (SvgCoder.CanDecode(data))
        {
            return ImageFileFormat.Svg;
        }

        if (FarbfeldCoder.CanDecode(data))
        {
            return ImageFileFormat.Farbfeld;
        }

        if (DpxCoder.CanDecode(data))
        {
            return ImageFileFormat.Dpx;
        }

        if (FitsCoder.CanDecode(data))
        {
            return ImageFileFormat.Fits;
        }

        if (CinCoder.CanDecode(data))
        {
            return ImageFileFormat.Cin;
        }

        if (DicomCoder.CanDecode(data))
        {
            return ImageFileFormat.Dicom;
        }

        if (Jpeg2000Coder.CanDecode(data))
        {
            return ImageFileFormat.Jpeg2000;
        }

        if (JxlCoder.CanDecode(data))
        {
            return ImageFileFormat.JpegXl;
        }

        if (HeifCoder.CanDecode(data))
        {
            return ImageFileFormat.Avif; // AVIF/HEIC both detected here
        }

        if (ExrCoder.CanDecode(data))
        {
            return ImageFileFormat.Exr;
        }

        if (SgiCoder.CanDecode(data))
        {
            return ImageFileFormat.Sgi;
        }

        if (SunCoder.CanDecode(data))
        {
            return ImageFileFormat.Sun;
        }

        if (PcxCoder.CanDecode(data))
        {
            return ImageFileFormat.Pcx;
        }

        if (XbmCoder.CanDecode(data))
        {
            return ImageFileFormat.Xbm;
        }

        if (XpmCoder.CanDecode(data))
        {
            return ImageFileFormat.Xpm;
        }

        if (TgaCoder.CanDecode(data))
        {
            return ImageFileFormat.Tga;
        }

        if (PnmCoder.CanDecode(data))
        {
            return ImageFileFormat.Pnm;
        }
        // WBMP has weak signature (0x00 0x00), check last
        if (WbmpCoder.CanDecode(data))
        {
            return ImageFileFormat.Wbmp;
        }

        return ImageFileFormat.Unknown;
    }

    /// <summary>
    /// Detect image format from file extension.
    /// </summary>
    public static ImageFileFormat DetectFromExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bmp" => ImageFileFormat.Bmp,
            ".tga" => ImageFileFormat.Tga,
            ".pnm" or ".ppm" or ".pgm" or ".pbm" => ImageFileFormat.Pnm,
            ".gif" => ImageFileFormat.Gif,
            ".jpg" or ".jpeg" => ImageFileFormat.Jpeg,
            ".png" => ImageFileFormat.Png,
            ".tif" or ".tiff" => ImageFileFormat.Tiff,
            ".webp" => ImageFileFormat.WebP,
            ".qoi" => ImageFileFormat.Qoi,
            ".ico" => ImageFileFormat.Ico,
            ".hdr" or ".pic" => ImageFileFormat.Hdr,
            ".psd" => ImageFileFormat.Psd,
            ".dds" => ImageFileFormat.Dds,
            ".svg" => ImageFileFormat.Svg,
            ".ff" or ".farbfeld" => ImageFileFormat.Farbfeld,
            ".wbmp" => ImageFileFormat.Wbmp,
            ".pcx" => ImageFileFormat.Pcx,
            ".xbm" => ImageFileFormat.Xbm,
            ".xpm" => ImageFileFormat.Xpm,
            ".dpx" => ImageFileFormat.Dpx,
            ".fits" or ".fit" or ".fts" => ImageFileFormat.Fits,
            ".cin" or ".cineon" => ImageFileFormat.Cin,
            ".raw" => ImageFileFormat.Raw,
            ".dcm" or ".dicom" => ImageFileFormat.Dicom,
            ".jp2" or ".j2k" or ".j2c" or ".jpc" => ImageFileFormat.Jpeg2000,
            ".jxl" => ImageFileFormat.JpegXl,
            ".avif" => ImageFileFormat.Avif,
            ".heic" or ".heif" => ImageFileFormat.Heic,
            ".exr" => ImageFileFormat.Exr,
            ".sgi" or ".rgb" or ".rgba" or ".bw" => ImageFileFormat.Sgi,
            ".pix" or ".alias" => ImageFileFormat.Pix,
            ".sun" or ".ras" => ImageFileFormat.Sun,
            ".pdf" => ImageFileFormat.Pdf,
            // Camera raw extensions
            ".cr2" or ".cr3" or ".crw" => ImageFileFormat.CameraRaw,
            ".nef" or ".nrw" => ImageFileFormat.CameraRaw,
            ".arw" or ".sr2" or ".srf" => ImageFileFormat.CameraRaw,
            ".dng" => ImageFileFormat.CameraRaw,
            ".orf" => ImageFileFormat.CameraRaw,
            ".rw2" => ImageFileFormat.CameraRaw,
            ".raf" => ImageFileFormat.CameraRaw,
            ".pef" => ImageFileFormat.CameraRaw,
            ".srw" => ImageFileFormat.CameraRaw,
            ".3fr" => ImageFileFormat.CameraRaw,
            ".iiq" => ImageFileFormat.CameraRaw,
            ".x3f" => ImageFileFormat.CameraRaw,
            ".dcr" or ".k25" or ".kdc" => ImageFileFormat.CameraRaw,
            ".mdc" => ImageFileFormat.CameraRaw,
            ".mef" => ImageFileFormat.CameraRaw,
            ".mos" => ImageFileFormat.CameraRaw,
            ".mrw" => ImageFileFormat.CameraRaw,
            ".rmf" => ImageFileFormat.CameraRaw,
            ".rwl" => ImageFileFormat.CameraRaw,
            ".erf" => ImageFileFormat.CameraRaw,
            ".fff" => ImageFileFormat.CameraRaw,
            ".sti" => ImageFileFormat.CameraRaw,
            _ => ImageFileFormat.Unknown
        };
    }

    /// <summary>
    /// Read an image from file, auto-detecting format from header.
    /// </summary>
    public static ImageFrame Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var format = DetectFormat(data);

        if (format == ImageFileFormat.Unknown)
        {
            format = DetectFromExtension(path);
        }

        return Decode(data, format);
    }

    /// <summary>
    /// Read an image from byte data with auto-detected format.
    /// </summary>
    public static ImageFrame Read(byte[] data)
    {
        var format = DetectFormat(data);
        return Decode(data, format);
    }

    /// <summary>
    /// Write an image to file, detecting format from extension.
    /// </summary>
    public static void Write(ImageFrame image, string path, int quality = 100)
    {
        var format = DetectFromExtension(path);
        byte[] data = quality < 100 && format is ImageFileFormat.JpegXl
            ? JxlCoder.EncodeLossy(image, quality)
            : Encode(image, format);
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// Decode image data in a specific format.
    /// </summary>
    public static ImageFrame Decode(byte[] data, ImageFileFormat format)
    {
        return format switch
        {
            ImageFileFormat.Png => PngCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Jpeg => JpegCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Gif => GifCoder.Read(new MemoryStream(data)),
            ImageFileFormat.WebP => WebpRead(data),
            ImageFileFormat.Tiff => TiffCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Bmp => BmpCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Tga => TgaCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Pnm => PnmCoder.Read(new MemoryStream(data)),
            ImageFileFormat.Psd => PsdCoder.Decode(data),
            ImageFileFormat.Dds => DdsCoder.Decode(data),
            ImageFileFormat.Qoi => QoiCoder.Decode(data),
            ImageFileFormat.Hdr => HdrCoder.Decode(data),
            ImageFileFormat.Ico => IcoCoder.Decode(data),
            ImageFileFormat.Svg => SvgCoder.Decode(data),
            ImageFileFormat.Farbfeld => FarbfeldCoder.Decode(data),
            ImageFileFormat.Wbmp => WbmpCoder.Decode(data),
            ImageFileFormat.Pcx => PcxCoder.Decode(data),
            ImageFileFormat.Xbm => XbmCoder.Decode(data),
            ImageFileFormat.Xpm => XpmCoder.Decode(data),
            ImageFileFormat.Dpx => DpxCoder.Decode(data),
            ImageFileFormat.Fits => FitsCoder.Decode(data),
            ImageFileFormat.Cin => CinCoder.Decode(data),
            ImageFileFormat.Dicom => DicomCoder.Decode(data),
            ImageFileFormat.Jpeg2000 => Jpeg2000Coder.Decode(data),
            ImageFileFormat.JpegXl => JxlCoder.Decode(data),
            ImageFileFormat.Avif or ImageFileFormat.Heic => HeifCoder.Decode(data),
            ImageFileFormat.Exr => ExrCoder.Decode(data),
            ImageFileFormat.Sgi => SgiCoder.Decode(data),
            ImageFileFormat.Pix => PixCoder.Decode(data),
            ImageFileFormat.Sun => SunCoder.Decode(data),
            ImageFileFormat.CameraRaw => CameraRawCoder.Decode(data),
            _ => throw new NotSupportedException($"Unsupported image format: {format}")
        };
    }

    /// <summary>
    /// Encode an image into a specific format.
    /// </summary>
    public static byte[] Encode(ImageFrame image, ImageFileFormat format)
    {
        return format switch
        {
            ImageFileFormat.Png => EncodeToStream(image, (img, s) => PngCoder.Write(img, s)),
            ImageFileFormat.Jpeg => EncodeToStream(image, (img, s) => JpegCoder.Write(img, s)),
            ImageFileFormat.Gif => EncodeToStream(image, (img, s) => GifCoder.Write(img, s)),
            ImageFileFormat.WebP => EncodeToStream(image, (img, s) => WebpCoder.Write(img, s)),
            ImageFileFormat.Tiff => EncodeToStream(image, (img, s) => TiffCoder.Write(img, s)),
            ImageFileFormat.Bmp => EncodeToStream(image, (img, s) => BmpCoder.Write(img, s)),
            ImageFileFormat.Tga => EncodeToStream(image, (img, s) => TgaCoder.Write(img, s)),
            ImageFileFormat.Pnm => EncodeToStream(image, (img, s) => PnmCoder.Write(img, s)),
            ImageFileFormat.Psd => PsdCoder.Encode(image),
            ImageFileFormat.Dds => DdsCoder.Encode(image),
            ImageFileFormat.Qoi => QoiCoder.Encode(image),
            ImageFileFormat.Hdr => HdrCoder.Encode(image),
            ImageFileFormat.Ico => IcoCoder.Encode(image),
            ImageFileFormat.Svg => SvgCoder.Encode(image),
            ImageFileFormat.Farbfeld => FarbfeldCoder.Encode(image),
            ImageFileFormat.Wbmp => WbmpCoder.Encode(image),
            ImageFileFormat.Pcx => PcxCoder.Encode(image),
            ImageFileFormat.Xbm => XbmCoder.Encode(image),
            ImageFileFormat.Xpm => XpmCoder.Encode(image),
            ImageFileFormat.Dpx => DpxCoder.Encode(image),
            ImageFileFormat.Fits => FitsCoder.Encode(image),
            ImageFileFormat.Cin => CinCoder.Encode(image),
            ImageFileFormat.Dicom => DicomCoder.Encode(image),
            ImageFileFormat.Jpeg2000 => Jpeg2000Coder.Encode(image),
            ImageFileFormat.JpegXl => JxlCoder.Encode(image),
            ImageFileFormat.Avif => HeifCoder.Encode(image, HeifContainerType.Avif),
            ImageFileFormat.Heic => HeifCoder.Encode(image, HeifContainerType.Heic),
            ImageFileFormat.Exr => ExrCoder.Encode(image),
            ImageFileFormat.Sgi => SgiCoder.Encode(image),
            ImageFileFormat.Pix => PixCoder.Encode(image),
            ImageFileFormat.Sun => SunCoder.Encode(image),
            ImageFileFormat.Pdf => PdfCoder.Encode(image),
            ImageFileFormat.CameraRaw => CameraRawCoder.Encode(image),
            _ => throw new NotSupportedException($"Unsupported image format: {format}")
        };
    }

    private static byte[] EncodeToStream(ImageFrame image, Action<ImageFrame, Stream> writer)
    {
        using var ms = new MemoryStream();
        writer(image, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Read an animated/multi-frame image as a sequence of frames.
    /// </summary>
    public static ImageSequence ReadSequence(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var format = DetectFormat(data);
        if (format == ImageFileFormat.Unknown)
            format = DetectFromExtension(path);

        return format switch
        {
            ImageFileFormat.Gif => GifCoder.ReadSequence(new MemoryStream(data)),
            ImageFileFormat.WebP => WebpCoder.ReadSequence(new MemoryStream(data)),
            _ => throw new NotSupportedException($"Multi-frame read not supported for: {format}")
        };
    }

    /// <summary>
    /// Write a multi-frame image sequence to file.
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, string path)
    {
        var format = DetectFromExtension(path);

        switch (format)
        {
            case ImageFileFormat.Gif:
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    GifCoder.WriteSequence(sequence, stream);
                break;
            case ImageFileFormat.WebP:
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                    WebpCoder.WriteSequence(sequence, stream);
                break;
            default:
                throw new NotSupportedException($"Multi-frame write not supported for: {format}");
        }
    }

    private static ImageFrame WebpRead(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return WebpCoder.Read(ms);
    }
}
