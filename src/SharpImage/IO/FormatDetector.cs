using System.Runtime.CompilerServices;

namespace SharpImage.IO;

/// <summary>
/// Detects image formats from magic bytes at the start of a stream or byte buffer.
/// </summary>
public static class FormatDetector
{
    public enum ImageFormat
    {
        Unknown,
        Bmp,
        Tga,
        Pnm,    // PBM, PGM, PPM, PAM, PFM
        Png,
        Jpeg,
        Gif,
        Tiff,
        WebP
    }

    /// <summary>
    /// Detects image format from the first bytes of data. Requires at least 12 bytes for reliable detection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length < 2)
        {
            return ImageFormat.Unknown;
        }

        // BMP: "BM"
        if (header[0] == 0x42 && header[1] == 0x4D)
        {
            return ImageFormat.Bmp;
        }

        // PNM: "P1"-"P7", "Pf", "PF", "Ph", "PH"
        if (header[0] == (byte)'P')
        {
            byte second = header[1];
            if (second >= (byte)'1' && second <= (byte)'7')
            {
                return ImageFormat.Pnm;
            }

            if (second == (byte)'f' || second == (byte)'F' ||
                second == (byte)'h' || second == (byte)'H')
            {
                return ImageFormat.Pnm;
            }
        }

        // PNG: 0x89 "PNG" 0x0D 0x0A 0x1A 0x0A
        if (header.Length >= 4 && header[0] == 0x89 &&
            header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            return ImageFormat.Png;
        }

        // JPEG: 0xFF 0xD8 0xFF
        if (header.Length >= 3 && header[0] == 0xFF &&
            header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        // GIF: "GIF87a" or "GIF89a"
        if (header.Length >= 4 && header[0] == (byte)'G' &&
            header[1] == (byte)'I' && header[2] == (byte)'F')
        {
            return ImageFormat.Gif;
        }

        // TIFF: "II" (little-endian) or "MM" (big-endian) + 42
        if (header.Length >= 4)
        {
            if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
                (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))
            {
                return ImageFormat.Tiff;
            }
        }

        // WebP: "RIFF" + ... + "WEBP"
        if (header.Length >= 12 && header[0] == (byte)'R' &&
            header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' &&
            header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return ImageFormat.WebP;
        }

        return ImageFormat.Unknown;
    }

    /// <summary>
    /// Detects image format from file extension.
    /// </summary>
    public static ImageFormat DetectFromExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bmp" or ".dib" => ImageFormat.Bmp,
            ".tga" or ".tpic" or ".vda" or ".icb" or ".vst" => ImageFormat.Tga,
            ".pnm" or ".pbm" or ".pgm" or ".ppm" or ".pam" or ".pfm" or ".phm" => ImageFormat.Pnm,
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
            ".gif" => ImageFormat.Gif,
            ".tif" or ".tiff" => ImageFormat.Tiff,
            ".webp" => ImageFormat.WebP,
            _ => ImageFormat.Unknown
        };
    }
}
