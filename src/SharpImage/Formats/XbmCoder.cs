// XBM (X BitMap) format coder — read and write.
// Text-based C header format for 1-bit bitmaps.
// #define name_width, #define name_height, static unsigned char name_bits[] = { hex... }

using SharpImage.Core;
using SharpImage.Image;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpImage.Formats;

public static partial class XbmCoder
{
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(data[..Math.Min(data.Length, 256)]);
        return text.Contains("#define") && text.Contains("_width");
    }

    public static ImageFrame Decode(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data);

        // Parse width and height
        var widthMatch = WidthRegex().Match(text);
        var heightMatch = HeightRegex().Match(text);
        if (!widthMatch.Success || !heightMatch.Success)
        {
            throw new InvalidDataException("Cannot parse XBM dimensions");
        }

        int width = int.Parse(widthMatch.Groups[1].Value);
        int height = int.Parse(heightMatch.Groups[1].Value);

        // Find hex data
        int braceStart = text.IndexOf('{');
        if (braceStart < 0)
        {
            throw new InvalidDataException("No data block in XBM");
        }

        int braceEnd = text.IndexOf('}', braceStart);
        if (braceEnd < 0)
        {
            braceEnd = text.Length;
        }

        string dataBlock = text[(braceStart + 1)..braceEnd];
        var hexValues = HexRegex().Matches(dataBlock);

        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, false);
        int channels = frame.NumberOfChannels;
        int bytesPerRow = (width + 7) / 8;

        int byteIdx = 0;
        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int bx = 0;bx < bytesPerRow && byteIdx < hexValues.Count;bx++, byteIdx++)
            {
                byte b = byte.Parse(hexValues[byteIdx].Groups[1].Value, NumberStyles.HexNumber);
                for (int bit = 0;bit < 8;bit++)
                {
                    int x = bx * 8 + bit;
                    if (x >= width)
                    {
                        break;
                    }

                    // XBM: LSB first, 1 = foreground (black), 0 = background (white)
                    bool isForeground = ((b >> bit) & 1) == 1;
                    ushort val = isForeground ? (ushort)0 : Quantum.MaxValue;

                    int offset = x * channels;
                    row[offset] = val;
                    if (channels > 1)
                    {
                        row[offset + 1] = val;
                    }

                    if (channels > 2)
                    {
                        row[offset + 2] = val;
                    }
                }
            }
        }

        return frame;
    }

    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int imgChannels = image.NumberOfChannels;
        int bytesPerRow = (w + 7) / 8;

        var sb = new StringBuilder();
        sb.AppendLine($"#define image_width {w}");
        sb.AppendLine($"#define image_height {h}");
        sb.Append("static unsigned char image_bits[] = {\n  ");

        int total = bytesPerRow * h;
        int written = 0;

        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int bx = 0;bx < bytesPerRow;bx++)
            {
                byte packed = 0;
                for (int bit = 0;bit < 8;bit++)
                {
                    int x = bx * 8 + bit;
                    if (x >= w)
                    {
                        break;
                    }

                    int offset = x * imgChannels;
                    ushort r = row[offset];
                    ushort g = imgChannels > 1 ? row[offset + 1] : r;
                    ushort b = imgChannels > 2 ? row[offset + 2] : r;
                    double luma = 0.299 * r + 0.587 * g + 0.114 * b;

                    // LSB first; dark pixels = foreground (1)
                    if (luma < Quantum.MaxValue / 2.0)
                    {
                        packed |= (byte)(1 << bit);
                    }
                }

                written++;
                sb.Append($"0x{packed:x2}");
                if (written < total)
                {
                    sb.Append(',');
                }

                if (written % 12 == 0 && written < total)
                {
                    sb.Append("\n  ");
                }
            }
        }

        sb.Append("\n};\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [GeneratedRegex(@"_width\s+(\d+)")]
    private static partial Regex WidthRegex();

    [GeneratedRegex(@"_height\s+(\d+)")]
    private static partial Regex HeightRegex();

    [GeneratedRegex(@"0[xX]([0-9a-fA-F]{1,2})")]
    private static partial Regex HexRegex();
}
