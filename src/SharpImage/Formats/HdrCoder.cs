// HDR (Radiance RGBE) format coder — read and write.
// Supports the .hdr/.pic format used for high dynamic range images.
// Implements both old-style and new-style RLE scanline encoding.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpImage.Formats;

/// <summary>
/// Reads and writes images in the Radiance HDR (RGBE) format. HDR stores floating-point color data using a shared
/// exponent per pixel.
/// </summary>
public static class HdrCoder
{
    private const int ExponentBias = 128;

    /// <summary>
    /// Detect HDR format by magic string.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return false;
        }

        return data[0] == '#' && data[1] == '?' &&
               (StartsWith(data, "#?RADIANCE"u8) || StartsWith(data, "#?RGBE"u8));
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        return data[..prefix.Length].SequenceEqual(prefix);
    }

    /// <summary>
    /// Decode an HDR file from byte data into an ImageFrame.
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid HDR file");
        }

        int pos = 0;
        double exposure = 1.0;
        int width = 0, height = 0;

        // Parse header
        while (pos < data.Length)
        {
            string line = ReadLine(data, ref pos);
            if (line.Length == 0)
            {
                break; // blank line ends header
            }

            if (line.StartsWith("EXPOSURE=", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(line.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double exp))
                {
                    exposure = exp;
                }
            }
        }

        // Parse resolution string
        string resLine = ReadLine(data, ref pos);
        ParseResolution(resLine, out width, out height);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid HDR resolution: {width}x{height}");
        }

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);

        // Decode scanlines
        var rgbeBuffer = new byte[width * 4];

        for (int y = 0;y < height;y++)
        {
            DecodeScanline(data, ref pos, rgbeBuffer, width);

            var row = frame.GetPixelRowForWrite(y);
            int channels = frame.NumberOfChannels;

            for (int x = 0;x < width;x++)
            {
                int rgbeIdx = x * 4;
                byte re = rgbeBuffer[rgbeIdx];
                byte ge = rgbeBuffer[rgbeIdx + 1];
                byte be = rgbeBuffer[rgbeIdx + 2];
                byte e = rgbeBuffer[rgbeIdx + 3];

                RgbeToFloat(re, ge, be, e, out float rf, out float gf, out float bf);

                // Apply exposure
                rf *= (float)exposure;
                gf *= (float)exposure;
                bf *= (float)exposure;

                // Tone-map to [0,1] using simple Reinhard operator
                rf = rf / (1f + rf);
                gf = gf / (1f + gf);
                bf = bf / (1f + bf);

                int offset = x * channels;
                row[offset] = Quantum.ScaleFromDouble(rf);
                if (channels > 1)
                {
                    row[offset + 1] = Quantum.ScaleFromDouble(gf);
                }

                if (channels > 2)
                {
                    row[offset + 2] = Quantum.ScaleFromDouble(bf);
                }
            }
        }

        return frame;
    }

    /// <summary>
    /// Encode an ImageFrame as HDR format bytes.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        int channels = image.NumberOfChannels;

        using var ms = new MemoryStream();

        // Write header
        byte[] headerBytes = Encoding.ASCII.GetBytes(
            "#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n");
        ms.Write(headerBytes);

        // Write resolution
        byte[] resBytes = Encoding.ASCII.GetBytes($"-Y {height} +X {width}\n");
        ms.Write(resBytes);

        // Encode scanlines
        var rgbe = new byte[width * 4];
        var scanline = new byte[width * 4 + width]; // max RLE size

        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);

            for (int x = 0;x < width;x++)
            {
                int offset = x * channels;
                float rf = (float)Quantum.ScaleToDouble(row[offset]);
                float gf = channels > 1 ? (float)Quantum.ScaleToDouble(row[offset + 1]) : rf;
                float bf = channels > 2 ? (float)Quantum.ScaleToDouble(row[offset + 2]) : rf;

                // Inverse Reinhard to get HDR values
                rf = rf / Math.Max(1f - rf, 1e-6f);
                gf = gf / Math.Max(1f - gf, 1e-6f);
                bf = bf / Math.Max(1f - bf, 1e-6f);

                FloatToRgbe(rf, gf, bf, out byte re, out byte ge, out byte be, out byte e);

                int idx = x * 4;
                rgbe[idx] = re;
                rgbe[idx + 1] = ge;
                rgbe[idx + 2] = be;
                rgbe[idx + 3] = e;
            }

            EncodeScanline(ms, rgbe, width);
        }

        return ms.ToArray();
    }

    #region RGBE conversion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RgbeToFloat(byte r, byte g, byte b, byte e,
        out float rf, out float gf, out float bf)
    {
        if (e == 0)
        {
            rf = gf = bf = 0;
            return;
        }

        // gamma = 2^(e - 128 - 8) = ldexp(1.0, e - 136)
        float gamma = MathF.Pow(2f, e - 136f);
        rf = r * gamma;
        gf = g * gamma;
        bf = b * gamma;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FloatToRgbe(float r, float g, float b,
        out byte re, out byte ge, out byte be, out byte e)
    {
        float maxVal = Math.Max(r, Math.Max(g, b));
        if (maxVal < 1e-32f)
        {
            re = ge = be = e = 0;
            return;
        }

        // Normalize: find exponent such that maxVal = mantissa * 2^exp
        int exp = 0;
        float mantissa = Frexp(maxVal, ref exp);
        float scale = 256f * mantissa / maxVal;

        re = (byte)Math.Clamp(r * scale, 0, 255);
        ge = (byte)Math.Clamp(g * scale, 0, 255);
        be = (byte)Math.Clamp(b * scale, 0, 255);
        e = (byte)Math.Clamp(exp + ExponentBias, 0, 255);
    }

    /// <summary>
    /// Equivalent to C's frexp: returns mantissa in [0.5, 1.0) and sets exponent.
    /// </summary>
    private static float Frexp(float value, ref int exponent)
    {
        if (value == 0f)
        {
            exponent = 0;
            return 0f;
        }
        int bits = BitConverter.SingleToInt32Bits(value);
        exponent = ((bits >> 23) & 0xFF) - 126;
        bits = (bits & ~(0xFF << 23)) | (126 << 23);
        return BitConverter.Int32BitsToSingle(bits);
    }

    #endregion

    #region Scanline encoding/decoding

    private static void DecodeScanline(ReadOnlySpan<byte> data, ref int pos, byte[] buffer, int width)
    {
        if (pos + 4 > data.Length)
        {
            throw new InvalidDataException("Unexpected end of HDR data");
        }

        // Check for new-style RLE
        if (width >= 8 && width <= 0x7FFF && pos + 3 < data.Length &&
            data[pos] == 2 && data[pos + 1] == 2)
        {
            int w = (data[pos + 2] << 8) | data[pos + 3];
            if (w == width)
            {
                pos += 4;
                // New-style: each channel encoded separately with RLE
                for (int ch = 0;ch < 4;ch++)
                {
                    int x = 0;
                    while (x < width)
                    {
                        if (pos >= data.Length)
                        {
                            break;
                        }

                        byte count = data[pos++];
                        if (count > 128)
                        {
                            // RLE run
                            int runLen = count - 128;
                            byte val = data[pos++];
                            for (int i = 0;i < runLen && x < width;i++, x++)
                            {
                                buffer[x * 4 + ch] = val;
                            }
                        }
                        else
                        {
                            // Literal
                            for (int i = 0;i < count && x < width;i++, x++)
                            {
                                buffer[x * 4 + ch] = data[pos++];
                            }
                        }
                    }
                }
                return;
            }
        }

        // Old-style: raw RGBE pixels
        for (int x = 0;x < width;x++)
        {
            if (pos + 3 >= data.Length)
            {
                break;
            }

            buffer[x * 4] = data[pos++];
            buffer[x * 4 + 1] = data[pos++];
            buffer[x * 4 + 2] = data[pos++];
            buffer[x * 4 + 3] = data[pos++];
        }
    }

    private static void EncodeScanline(MemoryStream ms, byte[] rgbe, int width)
    {
        if (width < 8 || width > 0x7FFF)
        {
            // Old-style: just write raw
            ms.Write(rgbe, 0, width * 4);
            return;
        }

        // New-style RLE header
        ms.WriteByte(2);
        ms.WriteByte(2);
        ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(width & 0xFF));

        // Encode each channel separately
        for (int ch = 0;ch < 4;ch++)
        {
            int x = 0;
            while (x < width)
            {
                // Look for runs
                int runStart = x;
                byte val = rgbe[x * 4 + ch];
                while (x < width && x - runStart < 127 && rgbe[x * 4 + ch] == val)
                {
                    x++;
                }

                int runLen = x - runStart;
                if (runLen >= 3)
                {
                    ms.WriteByte((byte)(runLen + 128));
                    ms.WriteByte(val);
                }
                else
                {
                    // Literal — find extent of non-run data
                    x = runStart;
                    int litStart = x;
                    while (x < width && x - litStart < 127)
                    {
                        // Check if a run of 3+ starts here
                        if (x + 2 < width &&
                            rgbe[x * 4 + ch] == rgbe[(x + 1) * 4 + ch] &&
                            rgbe[x * 4 + ch] == rgbe[(x + 2) * 4 + ch])
                        {
                            break;
                        }

                        x++;
                    }

                    int litLen = x - litStart;
                    if (litLen == 0)
                    {
                        litLen = 1; // avoid zero-length
                    }

                    if (litLen > 0)
                    {
                        ms.WriteByte((byte)litLen);
                        for (int i = litStart;i < litStart + litLen;i++)
                        {
                            ms.WriteByte(rgbe[i * 4 + ch]);
                        }
                    }
                    if (x == litStart)
                    {
                        x++; // prevent infinite loop
                    }
                }
            }
        }
    }

    #endregion

    #region Header parsing

    private static string ReadLine(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != '\n')
        {
            pos++;
        }

        int end = pos;
        if (pos < data.Length)
        {
            pos++; // skip newline
        }

        if (end > start && data[end - 1] == '\r')
        {
            end--;
        }

        return Encoding.ASCII.GetString(data[start..end]);
    }

    private static void ParseResolution(string line, out int width, out int height)
    {
        width = height = 0;
        // Format: "-Y <height> +X <width>" (most common)
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0;i < parts.Length - 1;i++)
        {
            if (parts[i] == "-Y" || parts[i] == "+Y")
            {
                int.TryParse(parts[i + 1], out height);
            }
            else if (parts[i] == "+X" || parts[i] == "-X")
            {
                int.TryParse(parts[i + 1], out width);
            }
        }
    }

    #endregion
}
