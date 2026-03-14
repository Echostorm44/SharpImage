// XPM (X PixMap) format coder — read and write.
// Text-based C source format with embedded color table and character-to-pixel mapping.
// Format: /* XPM */ header, "width height colors cpp" line, color defs, pixel rows.

using SharpImage.Core;
using SharpImage.Image;
using System.Globalization;
using System.Text;

namespace SharpImage.Formats;

public static class XpmCoder
{
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(data[..Math.Min(data.Length, 64)]);
        return text.Contains("/* XPM */") || text.Contains("/*XPM*/");
    }

    public static ImageFrame Decode(byte[] data)
    {
        string text = Encoding.ASCII.GetString(data);
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid XPM file");
        }

        // Extract all quoted strings
        var strings = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int qStart = text.IndexOf('"', pos);
            if (qStart < 0)
            {
                break;
            }

            int qEnd = text.IndexOf('"', qStart + 1);
            if (qEnd < 0)
            {
                break;
            }

            strings.Add(text[(qStart + 1)..qEnd]);
            pos = qEnd + 1;
        }

        if (strings.Count < 2)
        {
            throw new InvalidDataException("XPM has insufficient data");
        }

        // First string: "width height colors cpp [hotx hoty]"
        var headerParts = strings[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int width = int.Parse(headerParts[0]);
        int height = int.Parse(headerParts[1]);
        int colorCount = int.Parse(headerParts[2]);
        int charsPerPixel = int.Parse(headerParts[3]);

        if (strings.Count < 1 + colorCount + height)
        {
            throw new InvalidDataException("XPM file truncated");
        }

        // Parse color definitions
        var colorTable = new Dictionary<string, (byte R, byte G, byte B, byte A)>();
        for (int i = 0;i < colorCount;i++)
        {
            string line = strings[1 + i];
            string key = line[..charsPerPixel];
            string rest = line[charsPerPixel..].Trim();

            // Parse "c <color>" (the 'c' visual type is standard color)
            byte r = 0, g = 0, b = 0, a = 255;
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0;p < parts.Length - 1;p++)
            {
                if (parts[p].Equals("c", StringComparison.OrdinalIgnoreCase))
                {
                    string colorStr = parts[p + 1].ToLowerInvariant();
                    if (colorStr == "none")
                    {
                        a = 0;
                    }
                    else if (colorStr.StartsWith('#'))
                    {
                        ParseHexColor(colorStr, out r, out g, out b);
                    }
                    else
                    {
                        ParseNamedColor(colorStr, out r, out g, out b);
                    }
                    break;
                }
            }

            colorTable[key] = (r, g, b, a);
        }

        bool hasTransparency = colorTable.Values.Any(c => c.A == 0);
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasTransparency);
        int channels = frame.NumberOfChannels;

        // Parse pixel rows
        for (int y = 0;y < height;y++)
        {
            string rowStr = strings[1 + colorCount + y];
            var row = frame.GetPixelRowForWrite(y);

            for (int x = 0;x < width;x++)
            {
                int charIdx = x * charsPerPixel;
                if (charIdx + charsPerPixel > rowStr.Length)
                {
                    break;
                }

                string key = rowStr[charIdx..(charIdx + charsPerPixel)];

                byte r = 0, g = 0, b = 0, a = 255;
                if (colorTable.TryGetValue(key, out var color))
                {
                    r = color.R;
                    g = color.G;
                    b = color.B;
                    a = color.A;
                }

                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(r);
                if (channels > 1)
                {
                    row[offset + 1] = Quantum.ScaleFromByte(g);
                }

                if (channels > 2)
                {
                    row[offset + 2] = Quantum.ScaleFromByte(b);
                }

                if (hasTransparency)
                {
                    row[offset + channels - 1] = Quantum.ScaleFromByte(a);
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
        bool hasAlpha = image.HasAlpha;

        // Collect unique colors (quantize to 8-bit)
        var colorSet = new Dictionary<uint, int>();
        var colorList = new List<uint>();

        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < w;x++)
            {
                int offset = x * imgChannels;
                byte r = Quantum.ScaleToByte(row[offset]);
                byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[offset + 1]) : r;
                byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[offset + 2]) : r;
                byte a = hasAlpha ? Quantum.ScaleToByte(row[offset + imgChannels - 1]) : (byte)255;
                uint key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                if (!colorSet.ContainsKey(key))
                {
                    colorSet[key] = colorList.Count;
                    colorList.Add(key);
                }
            }
        }

        int colorCount = colorList.Count;
        int charsPerPixel = colorCount <= 92 ? 1 : colorCount <= 8464 ? 2 : 3;

        var sb = new StringBuilder();
        sb.AppendLine("/* XPM */");
        sb.AppendLine("static char *image_xpm[] = {");
        sb.AppendLine($"\"{w} {h} {colorCount} {charsPerPixel}\",");

        // Color definitions
        const string charSet = " .+@#$%&*=-;:>,<1234567890qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM!~^/()_`'|{}[]";
        for (int i = 0;i < colorCount;i++)
        {
            string key = GetColorKey(i, charsPerPixel, charSet);
            uint c = colorList[i];
            byte a = (byte)(c >> 24);
            byte r = (byte)((c >> 16) & 0xFF);
            byte g = (byte)((c >> 8) & 0xFF);
            byte b = (byte)(c & 0xFF);

            if (a == 0)
            {
                sb.AppendLine($"\"{key}\tc None\",");
            }
            else
            {
                sb.AppendLine($"\"{key}\tc #{r:X2}{g:X2}{b:X2}\",");
            }
        }

        // Pixel rows
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            sb.Append('"');
            for (int x = 0;x < w;x++)
            {
                int offset = x * imgChannels;
                byte r = Quantum.ScaleToByte(row[offset]);
                byte g = imgChannels > 1 ? Quantum.ScaleToByte(row[offset + 1]) : r;
                byte b = imgChannels > 2 ? Quantum.ScaleToByte(row[offset + 2]) : r;
                byte a = hasAlpha ? Quantum.ScaleToByte(row[offset + imgChannels - 1]) : (byte)255;
                uint key = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                sb.Append(GetColorKey(colorSet[key], charsPerPixel, charSet));
            }
            sb.Append(y < h - 1 ? "\"," : "\"");
            sb.AppendLine();
        }

        sb.AppendLine("};");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string GetColorKey(int index, int cpp, string charSet)
    {
        if (cpp == 1)
        {
            return charSet[index % charSet.Length].ToString();
        }

        var chars = new char[cpp];
        for (int i = cpp - 1;i >= 0;i--)
        {
            chars[i] = charSet[index % charSet.Length];
            index /= charSet.Length;
        }
        return new string(chars);
    }

    private static void ParseHexColor(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
        {
            r = (byte)(Convert.ToByte(hex[..1], 16) * 17);
            g = (byte)(Convert.ToByte(hex[1..2], 16) * 17);
            b = (byte)(Convert.ToByte(hex[2..3], 16) * 17);
        }
        else if (hex.Length >= 6)
        {
            r = Convert.ToByte(hex[..2], 16);
            g = Convert.ToByte(hex[2..4], 16);
            b = Convert.ToByte(hex[4..6], 16);
        }
    }

    private static void ParseNamedColor(string name, out byte r, out byte g, out byte b)
    {
        (r, g, b) = name switch
        {
            "black" => ((byte)0, (byte)0, (byte)0),
            "white" => ((byte)255, (byte)255, (byte)255),
            "red" => ((byte)255, (byte)0, (byte)0),
            "green" => ((byte)0, (byte)128, (byte)0),
            "blue" => ((byte)0, (byte)0, (byte)255),
            "yellow" => ((byte)255, (byte)255, (byte)0),
            "cyan" => ((byte)0, (byte)255, (byte)255),
            "magenta" => ((byte)255, (byte)0, (byte)255),
            "gray" or "grey" => ((byte)128, (byte)128, (byte)128),
            _ => ((byte)0, (byte)0, (byte)0)
        };
    }
}
