using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

using SharpImage.Core;
using SharpImage.Image;

/// <summary>
/// Reads and writes Netpbm formats: PBM (P1/P4), PGM (P2/P5), PPM (P3/P6), PAM (P7). Ported from ImageMagick
/// MagickCore/coders/pnm.c.
/// </summary>
public static class PnmCoder
{
    /// <summary>
    /// Detect PNM format by 'P' magic + format digit (1-6).
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 'P' && data[1] >= '1' && data[1] <= '6';

    // --- Reading ---

    public static ImageFrame Read(Stream stream)
    {
        int magicP = stream.ReadByte();
        if (magicP != 'P')
        {
            throw new InvalidDataException("Not a valid PNM file: missing 'P' magic byte.");
        }

        int formatChar = stream.ReadByte();
        if (formatChar < 0)
        {
            throw new InvalidDataException("Unexpected end of PNM header.");
        }

        return formatChar switch
        {
            '1' => ReadPbmAscii(stream),
            '2' => ReadPgmAscii(stream),
            '3' => ReadPpmAscii(stream),
            '4' => ReadPbmBinary(stream),
            '5' => ReadPgmBinary(stream),
            '6' => ReadPpmBinary(stream),
            '7' => ReadPam(stream),
            _ => throw new NotSupportedException($"PNM format P{(char)formatChar} is not supported.")
        };
    }

    public static ImageFrame Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    // --- Writing ---

    public static void Write(ImageFrame image, Stream stream, PnmFormat format = PnmFormat.PpmBinary)
    {
        switch (format)
        {
            case PnmFormat.PbmAscii:
                WritePbmAscii(image, stream);
                break;
            case PnmFormat.PgmAscii:
                WritePgmAscii(image, stream);
                break;
            case PnmFormat.PpmAscii:
                WritePpmAscii(image, stream);
                break;
            case PnmFormat.PbmBinary:
                WritePbmBinary(image, stream);
                break;
            case PnmFormat.PgmBinary:
                WritePgmBinary(image, stream);
                break;
            case PnmFormat.PpmBinary:
                WritePpmBinary(image, stream);
                break;
            case PnmFormat.Pam:
                WritePam(image, stream);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static void Write(ImageFrame image, string path, PnmFormat format = PnmFormat.PpmBinary)
    {
        using var stream = File.Create(path);
        Write(image, stream, format);
    }

    // --- P1: PBM ASCII ---

    private static ImageFrame ReadPbmAscii(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);
        frame.FormatName = "PBM";

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int val = ReadAsciiInt(stream);
                // PBM: 0=white, 1=black (inverted)
                row[x] = val == 0 ? Quantum.MaxValue : (ushort)0;
            }
        }

        return frame;
    }

    private static void WritePbmAscii(ImageFrame image, Stream stream)
    {
        WriteHeaderLine(stream, $"P1\n{image.Columns} {image.Rows}\n");
        var sb = new StringBuilder();

        for (int y = 0;y < image.Rows;y++)
        {
            var row = image.GetPixelRow(y);
            int channels = image.NumberOfChannels;
            sb.Clear();
            for (int x = 0;x < image.Columns;x++)
            {
                if (x > 0)
                {
                    sb.Append(' ');
                }

                ushort gray = channels >= 3
                    ? PixelIntensity(row, x, channels)
                    : row[x * channels];
                sb.Append(gray < Quantum.MaxValue / 2 ? '1' : '0');
            }
            sb.Append('\n');
            WriteAscii(stream, sb.ToString());
        }
    }

    // --- P2: PGM ASCII ---

    private static ImageFrame ReadPgmAscii(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);
        int maxval = ReadHeaderInt(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);
        frame.FormatName = "PGM";

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int val = ReadAsciiInt(stream);
                row[x] = ScaleToQuantum(val, maxval);
            }
        }

        return frame;
    }

    private static void WritePgmAscii(ImageFrame image, Stream stream)
    {
        WriteHeaderLine(stream, $"P2\n{image.Columns} {image.Rows}\n{(Quantum.Depth <= 8 ? 255 : 65535)}\n");
        var sb = new StringBuilder();

        for (int y = 0;y < image.Rows;y++)
        {
            var row = image.GetPixelRow(y);
            int channels = image.NumberOfChannels;
            sb.Clear();
            for (int x = 0;x < image.Columns;x++)
            {
                if (x > 0)
                {
                    sb.Append(' ');
                }

                ushort gray = channels >= 3
                    ? PixelIntensity(row, x, channels)
                    : row[x * channels];
                sb.Append(Quantum.Depth <= 8 ? Quantum.ScaleToByte(gray) : gray);
            }
            sb.Append('\n');
            WriteAscii(stream, sb.ToString());
        }
    }

    // --- P3: PPM ASCII ---

    private static ImageFrame ReadPpmAscii(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);
        int maxval = ReadHeaderInt(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        frame.FormatName = "PPM";

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                int offset = x * 3;
                row[offset] = ScaleToQuantum(ReadAsciiInt(stream), maxval);     // R
                row[offset + 1] = ScaleToQuantum(ReadAsciiInt(stream), maxval); // G
                row[offset + 2] = ScaleToQuantum(ReadAsciiInt(stream), maxval); // B
            }
        }

        return frame;
    }

    private static void WritePpmAscii(ImageFrame image, Stream stream)
    {
        WriteHeaderLine(stream, $"P3\n{image.Columns} {image.Rows}\n{(Quantum.Depth <= 8 ? 255 : 65535)}\n");
        var sb = new StringBuilder();

        for (int y = 0;y < image.Rows;y++)
        {
            var row = image.GetPixelRow(y);
            int channels = image.NumberOfChannels;
            sb.Clear();
            for (int x = 0;x < image.Columns;x++)
            {
                if (x > 0)
                {
                    sb.Append(' ');
                }

                int offset = x * channels;
                ushort r, g, b;
                if (channels >= 3)
                {
                    r = row[offset];
                    g = row[offset + 1];
                    b = row[offset + 2];
                }
                else
                {
                    r = g = b = row[offset];
                }
                if (Quantum.Depth <= 8)
                {
#pragma warning disable CS0162 // Quantum.Depth is a compile-time constant
                    sb.Append($"{Quantum.ScaleToByte(r)} {Quantum.ScaleToByte(g)} {Quantum.ScaleToByte(b)}");
                }
#pragma warning restore CS0162
                else
                {
                    sb.Append($"{r} {g} {b}");
                }
            }
            sb.Append('\n');
            WriteAscii(stream, sb.ToString());
        }
    }

    // --- P4: PBM Binary ---

    private static ImageFrame ReadPbmBinary(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);
        SkipSingleWhitespace(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);
        frame.FormatName = "PBM";

        int bytesPerRow = (width + 7) / 8;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);
        try
        {
            for (int y = 0;y < height;y++)
            {
                int read = stream.ReadAtLeast(rowBuffer.AsSpan(0, bytesPerRow), bytesPerRow, false);
                if (read < bytesPerRow)
                {
                    throw new InvalidDataException("Unexpected end of PBM data.");
                }

                var pixels = frame.GetPixelRowForWrite(y);
                for (int x = 0;x < width;x++)
                {
                    int byteIndex = x >> 3;
                    int bitIndex = 7 - (x & 7);
                    bool isBlack = ((rowBuffer[byteIndex] >> bitIndex) & 1) == 1;
                    pixels[x] = isBlack ? (ushort)0 : Quantum.MaxValue;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        return frame;
    }

    private static void WritePbmBinary(ImageFrame image, Stream stream)
    {
        WriteHeaderLine(stream, $"P4\n{image.Columns} {image.Rows}\n");

        int bytesPerRow = ((int)image.Columns + 7) / 8;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);
        try
        {
            for (int y = 0;y < image.Rows;y++)
            {
                Array.Clear(rowBuffer, 0, bytesPerRow);
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                for (int x = 0;x < image.Columns;x++)
                {
                    ushort gray = channels >= 3
                        ? PixelIntensity(row, x, channels)
                        : row[x * channels];
                    if (gray < Quantum.MaxValue / 2) // black
                    {
                        int byteIndex = x >> 3;
                        int bitIndex = 7 - (x & 7);
                        rowBuffer[byteIndex] |= (byte)(1 << bitIndex);
                    }
                }
                stream.Write(rowBuffer, 0, bytesPerRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    // --- P5: PGM Binary ---

    private static ImageFrame ReadPgmBinary(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);
        int maxval = ReadHeaderInt(stream);
        SkipSingleWhitespace(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.Gray, false);
        frame.FormatName = "PGM";

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = width * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < height;y++)
            {
                int read = stream.ReadAtLeast(rowBuffer.AsSpan(0, bytesPerRow), bytesPerRow, false);
                if (read < bytesPerRow)
                {
                    throw new InvalidDataException("Unexpected end of PGM data.");
                }

                var pixels = frame.GetPixelRowForWrite(y);
                for (int x = 0;x < width;x++)
                {
                    int val;
                    if (is16Bit)
                    {
                        val = (rowBuffer[x * 2] << 8) | rowBuffer[x * 2 + 1]; // big-endian
                    }
                    else
                    {
                        val = rowBuffer[x];
                    }

                    pixels[x] = ScaleToQuantum(val, maxval);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        return frame;
    }

    private static void WritePgmBinary(ImageFrame image, Stream stream)
    {
        int maxval = Quantum.Depth <= 8 ? 255 : 65535;
        WriteHeaderLine(stream, $"P5\n{image.Columns} {image.Rows}\n{maxval}\n");

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = (int)image.Columns * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < image.Rows;y++)
            {
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                for (int x = 0;x < image.Columns;x++)
                {
                    ushort gray = channels >= 3
                        ? PixelIntensity(row, x, channels)
                        : row[x * channels];

                    if (is16Bit)
                    {
                        rowBuffer[x * 2] = (byte)(gray >> 8);     // big-endian
                        rowBuffer[x * 2 + 1] = (byte)(gray & 0xFF);
                    }
                    else
                    {
                        rowBuffer[x] = Quantum.ScaleToByte(gray);
                    }
                }
                stream.Write(rowBuffer, 0, bytesPerRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    // --- P6: PPM Binary ---

    private static ImageFrame ReadPpmBinary(Stream stream)
    {
        int width = ReadHeaderInt(stream);
        int height = ReadHeaderInt(stream);
        int maxval = ReadHeaderInt(stream);
        SkipSingleWhitespace(stream);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        frame.FormatName = "PPM";

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = width * 3 * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < height;y++)
            {
                int read = stream.ReadAtLeast(rowBuffer.AsSpan(0, bytesPerRow), bytesPerRow, false);
                if (read < bytesPerRow)
                {
                    throw new InvalidDataException("Unexpected end of PPM data.");
                }

                var pixels = frame.GetPixelRowForWrite(y);
                for (int x = 0;x < width;x++)
                {
                    int srcOffset = x * 3 * bytesPerSample;
                    int dstOffset = x * 3;

                    if (is16Bit)
                    {
                        pixels[dstOffset] = ScaleToQuantum(
                            (rowBuffer[srcOffset] << 8) | rowBuffer[srcOffset + 1], maxval);
                        pixels[dstOffset + 1] = ScaleToQuantum(
                            (rowBuffer[srcOffset + 2] << 8) | rowBuffer[srcOffset + 3], maxval);
                        pixels[dstOffset + 2] = ScaleToQuantum(
                            (rowBuffer[srcOffset + 4] << 8) | rowBuffer[srcOffset + 5], maxval);
                    }
                    else
                    {
                        pixels[dstOffset] = ScaleToQuantum(rowBuffer[srcOffset], maxval);
                        pixels[dstOffset + 1] = ScaleToQuantum(rowBuffer[srcOffset + 1], maxval);
                        pixels[dstOffset + 2] = ScaleToQuantum(rowBuffer[srcOffset + 2], maxval);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        return frame;
    }

    private static void WritePpmBinary(ImageFrame image, Stream stream)
    {
        int maxval = Quantum.Depth <= 8 ? 255 : 65535;
        WriteHeaderLine(stream, $"P6\n{image.Columns} {image.Rows}\n{maxval}\n");

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = (int)image.Columns * 3 * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < image.Rows;y++)
            {
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                for (int x = 0;x < image.Columns;x++)
                {
                    int srcOffset = x * channels;
                    int dstOffset = x * 3 * bytesPerSample;
                    ushort r, g, b;

                    if (channels >= 3)
                    {
                        r = row[srcOffset];
                        g = row[srcOffset + 1];
                        b = row[srcOffset + 2];
                    }
                    else
                    {
                        r = g = b = row[srcOffset];
                    }

                    if (is16Bit)
                    {
                        rowBuffer[dstOffset] = (byte)(r >> 8);
                        rowBuffer[dstOffset + 1] = (byte)(r & 0xFF);
                        rowBuffer[dstOffset + 2] = (byte)(g >> 8);
                        rowBuffer[dstOffset + 3] = (byte)(g & 0xFF);
                        rowBuffer[dstOffset + 4] = (byte)(b >> 8);
                        rowBuffer[dstOffset + 5] = (byte)(b & 0xFF);
                    }
                    else
                    {
                        rowBuffer[dstOffset] = Quantum.ScaleToByte(r);
                        rowBuffer[dstOffset + 1] = Quantum.ScaleToByte(g);
                        rowBuffer[dstOffset + 2] = Quantum.ScaleToByte(b);
                    }
                }
                stream.Write(rowBuffer, 0, bytesPerRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    // --- P7: PAM ---

    private static ImageFrame ReadPam(Stream stream)
    {
        int width = 0, height = 0, depth = 0, maxval = 255;
        string tupleType = "";

        // Read header lines until ENDHDR
        while (true)
        {
            string? line = ReadLine(stream);
            if (line == null)
            {
                throw new InvalidDataException("Unexpected end of PAM header.");
            }

            line = line.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.Equals("ENDHDR", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            int spaceIndex = line.IndexOf(' ');
            if (spaceIndex < 0)
            {
                continue;
            }

            string keyword = line[..spaceIndex].ToUpperInvariant();
            string value = line[(spaceIndex + 1)..].Trim();

            switch (keyword)
            {
                case "WIDTH":
                    width = int.Parse(value);
                    break;
                case "HEIGHT":
                    height = int.Parse(value);
                    break;
                case "DEPTH":
                    depth = int.Parse(value);
                    break;
                case "MAXVAL":
                    maxval = int.Parse(value);
                    break;
                case "TUPLTYPE":
                    tupleType = value.ToUpperInvariant();
                    break;
            }
        }

        if (width <= 0 || height <= 0 || depth <= 0 || maxval <= 0)
        {
            throw new InvalidDataException("Invalid PAM header values.");
        }

        bool hasAlpha = tupleType.Contains("ALPHA", StringComparison.OrdinalIgnoreCase);
        bool isGray = tupleType.Contains("GRAYSCALE") || tupleType.Contains("BLACKANDWHITE");
        var colorspace = isGray ? ColorspaceType.Gray : ColorspaceType.SRGB;

        var frame = new ImageFrame();
        frame.Initialize(width, height, colorspace, hasAlpha);
        frame.FormatName = "PAM";

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = width * depth * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < height;y++)
            {
                int read = stream.ReadAtLeast(rowBuffer.AsSpan(0, bytesPerRow), bytesPerRow, false);
                if (read < bytesPerRow)
                {
                    throw new InvalidDataException("Unexpected end of PAM data.");
                }

                var pixels = frame.GetPixelRowForWrite(y);
                int outChannels = frame.NumberOfChannels;

                for (int x = 0;x < width;x++)
                {
                    int srcBase = x * depth * bytesPerSample;
                    int dstBase = x * outChannels;

                    for (int ch = 0;ch < Math.Min(depth, outChannels);ch++)
                    {
                        int val;
                        if (is16Bit)
                        {
                            val = (rowBuffer[srcBase + ch * 2] << 8) | rowBuffer[srcBase + ch * 2 + 1];
                        }
                        else
                        {
                            val = rowBuffer[srcBase + ch];
                        }

                        pixels[dstBase + ch] = ScaleToQuantum(val, maxval);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }

        return frame;
    }

    private static void WritePam(ImageFrame image, Stream stream)
    {
        int depth = image.NumberOfChannels;
        int maxval = Quantum.Depth <= 8 ? 255 : 65535;

        string tupleType;
        if (image.Colorspace == ColorspaceType.Gray)
        {
            tupleType = image.HasAlpha ? "GRAYSCALE_ALPHA" : "GRAYSCALE";
        }
        else
        {
            tupleType = image.HasAlpha ? "RGB_ALPHA" : "RGB";
        }

        var header = $"P7\nWIDTH {image.Columns}\nHEIGHT {image.Rows}\nDEPTH {depth}\nMAXVAL {maxval}\nTUPLTYPE {tupleType}\nENDHDR\n";
        WriteAscii(stream, header);

        bool is16Bit = maxval > 255;
        int bytesPerSample = is16Bit ? 2 : 1;
        int bytesPerRow = (int)image.Columns * depth * bytesPerSample;
        byte[] rowBuffer = ArrayPool<byte>.Shared.Rent(bytesPerRow);

        try
        {
            for (int y = 0;y < image.Rows;y++)
            {
                var row = image.GetPixelRow(y);
                int channels = image.NumberOfChannels;

                for (int x = 0;x < image.Columns;x++)
                {
                    int srcBase = x * channels;
                    int dstBase = x * depth * bytesPerSample;

                    for (int ch = 0;ch < depth;ch++)
                    {
                        ushort val = ch < channels ? row[srcBase + ch] : Quantum.MaxValue;

                        if (is16Bit)
                        {
                            rowBuffer[dstBase + ch * 2] = (byte)(val >> 8);
                            rowBuffer[dstBase + ch * 2 + 1] = (byte)(val & 0xFF);
                        }
                        else
                        {
                            rowBuffer[dstBase + ch] = Quantum.ScaleToByte(val);
                        }
                    }
                }
                stream.Write(rowBuffer, 0, bytesPerRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rowBuffer);
        }
    }

    // --- Header parsing helpers ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ScaleToQuantum(int value, int maxval)
    {
        if (maxval == 255)
        {
            return Quantum.ScaleFromByte((byte)Math.Clamp(value, 0, 255));
        }

        if (maxval == 65535)
        {
            return (ushort)Math.Clamp(value, 0, 65535);
        }

        return (ushort)Math.Clamp((long)value * Quantum.MaxValue / maxval, 0, Quantum.MaxValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort PixelIntensity(ReadOnlySpan<ushort> row, int x, int channels)
    {
        int offset = x * channels;
        // Rec. 709 luma: 0.2126R + 0.7152G + 0.0722B
        return (ushort)Math.Clamp(
            (int)(0.2126 * row[offset] + 0.7152 * row[offset + 1] + 0.0722 * row[offset + 2]),
            0, Quantum.MaxValue);
    }

    private static int ReadHeaderInt(Stream stream)
    {
        SkipWhitespaceAndComments(stream);
        int value = 0;
        bool foundDigit = false;

        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                if (foundDigit)
                {
                    return value;
                }

                throw new InvalidDataException("Unexpected end of PNM header.");
            }

            if (b >= '0' && b <= '9')
            {
                value = value * 10 + (b - '0');
                foundDigit = true;
            }
            else if (foundDigit)
            {
                return value;
            }
            else if (b == '#')
            {
                SkipToEndOfLine(stream);
            }
            // else skip whitespace
        }
    }

    private static int ReadAsciiInt(Stream stream)
    {
        SkipWhitespaceAndComments(stream);
        int value = 0;
        bool foundDigit = false;

        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                if (foundDigit)
                {
                    return value;
                }

                throw new InvalidDataException("Unexpected end of PNM data.");
            }

            if (b >= '0' && b <= '9')
            {
                value = value * 10 + (b - '0');
                foundDigit = true;
            }
            else if (foundDigit)
            {
                return value;
            }
        }
    }

    private static void SkipWhitespaceAndComments(Stream stream)
    {
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return;
            }

            if (b == '#')
            {
                SkipToEndOfLine(stream);
                continue;
            }

            if (b != ' ' && b != '\t' && b != '\r' && b != '\n')
            {
                // Put it back — we need a seekable stream or we track position
                if (stream.CanSeek)
                {
                    stream.Seek(-1, SeekOrigin.Current);
                }

                return;
            }
        }
    }

    private static void SkipSingleWhitespace(Stream stream)
    {
        // After the last header integer, there's exactly one whitespace char before binary data
        // We already consumed it in ReadHeaderInt, so this is a no-op in most cases
        // But for P4 which has no maxval, we need to consume it
    }

    private static void SkipToEndOfLine(Stream stream)
    {
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0 || b == '\n')
            {
                return;
            }
        }
    }

    private static string? ReadLine(Stream stream)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            if (b == '\n')
            {
                return sb.ToString();
            }

            if (b != '\r')
            {
                sb.Append((char)b);
            }
        }
    }

    private static void WriteHeaderLine(Stream stream, string text)
    {
        WriteAscii(stream, text);
    }

    private static void WriteAscii(Stream stream, string text)
    {
        Span<byte> bytes = stackalloc byte[Math.Min(text.Length, 1024)];
        if (text.Length <= 1024)
        {
            Encoding.ASCII.GetBytes(text, bytes);
            stream.Write(bytes);
        }
        else
        {
            stream.Write(Encoding.ASCII.GetBytes(text));
        }
    }
}

public enum PnmFormat
{
    PbmAscii,    // P1
    PgmAscii,    // P2
    PpmAscii,    // P3
    PbmBinary,   // P4
    PgmBinary,   // P5
    PpmBinary,   // P6
    Pam          // P7
}
