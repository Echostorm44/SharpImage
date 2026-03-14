// SVG (Scalable Vector Graphics) format coder — read (rasterize) and write.
// Renders a subset of SVG 1.1 (shapes, paths, fills, strokes, transforms)
// using our DrawingContext engine for rasterization.
// Write produces SVG from an ImageFrame as an embedded PNG in a data URI.

using SharpImage.Core;
using SharpImage.Drawing;
using SharpImage.Image;
using System.Globalization;
using System.Text;
using System.Xml;

namespace SharpImage.Formats;

/// <summary>
/// Reads SVG files by parsing and rasterizing into an ImageFrame. Writes SVG files by encoding the image as an embedded
/// PNG data URI.
/// </summary>
public static class SvgCoder
{
    /// <summary>
    /// Detect SVG by looking for XML/SVG markers.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return false;
        }
        // Check for XML declaration or <svg tag
        var text = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 512)]);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("<?xml", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("svg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rasterize an SVG file into an ImageFrame. Supports: rect, circle, ellipse, line, polyline, polygon, path
    /// elements.
    /// </summary>
    public static ImageFrame Decode(byte[] data, int renderWidth = 0, int renderHeight = 0)
    {
        string svgText = Encoding.UTF8.GetString(data);
        var doc = new XmlDocument();
        doc.LoadXml(svgText);

        var svgNode = FindSvgElement(doc);
        if (svgNode == null)
        {
            throw new InvalidDataException("No <svg> element found");
        }

        // Parse dimensions
        int width = renderWidth > 0 ? renderWidth : ParseDimension(svgNode.GetAttribute("width"), 256);
        int height = renderHeight > 0 ? renderHeight : ParseDimension(svgNode.GetAttribute("height"), 256);

        // Parse viewBox if present
        string viewBox = svgNode.GetAttribute("viewBox") ?? "";
        float vbX = 0, vbY = 0, vbW = width, vbH = height;
        if (!string.IsNullOrEmpty(viewBox))
        {
            var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                vbX = ParseFloat(parts[0]);
                vbY = ParseFloat(parts[1]);
                vbW = ParseFloat(parts[2]);
                vbH = ParseFloat(parts[3]);
            }
        }

        // Create canvas
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);

        // Fill with white background
        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            int ch = frame.NumberOfChannels;
            for (int x = 0;x < width;x++)
            {
                int offset = x * ch;
                row[offset] = Quantum.MaxValue;
                row[offset + 1] = Quantum.MaxValue;
                row[offset + 2] = Quantum.MaxValue;
                row[offset + ch - 1] = Quantum.MaxValue;
            }
        }

        var ctx = new DrawingContext(frame);

        // Apply viewBox scaling
        float scaleX = width / vbW;
        float scaleY = height / vbH;
        if (Math.Abs(scaleX - 1f) > 0.001f || Math.Abs(scaleY - 1f) > 0.001f)
        {
            ctx.Scale(scaleX, scaleY);
        }

        if (Math.Abs(vbX) > 0.001f || Math.Abs(vbY) > 0.001f)
        {
            ctx.Translate(-vbX, -vbY);
        }

        // Render child elements
        RenderChildren(svgNode, ctx);

        return frame;
    }

    /// <summary>
    /// Encode an ImageFrame as a minimal SVG with the image embedded as PNG data URI.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;

        // Encode image as PNG bytes
        using var pngStream = new MemoryStream();
        PngCoder.Write(image, pngStream);
        string base64 = Convert.ToBase64String(pngStream.ToArray());

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" " +
                       $"xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                       $"width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">");
        sb.AppendLine($"  <image width=\"{w}\" height=\"{h}\" " +
                       $"href=\"data:image/png;base64,{base64}\"/>");
        sb.AppendLine("</svg>");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    #region SVG rendering

    private static void RenderChildren(XmlNode parent, DrawingContext ctx)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child is not XmlElement elem)
            {
                continue;
            }

            string tag = elem.LocalName.ToLowerInvariant();

            // Apply transform
            string transformAttr = elem.GetAttribute("transform");
            if (!string.IsNullOrEmpty(transformAttr))
            {
                ctx.PushState();
            }

            ApplyTransform(ctx, transformAttr);

            // Parse common style attributes
            ParseStyle(elem, out var fill, out var stroke, out float strokeWidth, out float opacity);

            switch (tag)
            {
                case "g":
                    RenderChildren(elem, ctx);
                    break;

                case "rect":
                    RenderRect(elem, ctx, fill, stroke, strokeWidth);
                    break;

                case "circle":
                    RenderCircle(elem, ctx, fill, stroke, strokeWidth);
                    break;

                case "ellipse":
                    RenderEllipse(elem, ctx, fill, stroke, strokeWidth);
                    break;

                case "line":
                    RenderLine(elem, ctx, stroke, strokeWidth);
                    break;

                case "polyline":
                case "polygon":
                    RenderPoly(elem, ctx, fill, stroke, strokeWidth, tag == "polygon");
                    break;

                case "path":
                    RenderPath(elem, ctx, fill, stroke, strokeWidth);
                    break;

                case "svg":
                    RenderChildren(elem, ctx);
                    break;
            }

            if (!string.IsNullOrEmpty(transformAttr))
            {
                ctx.PopState();
            }
        }
    }

    private static void RenderRect(XmlElement elem, DrawingContext ctx,
        DrawColor? fill, DrawColor? stroke, float strokeWidth)
    {
        float x = GetFloatAttr(elem, "x");
        float y = GetFloatAttr(elem, "y");
        float w = GetFloatAttr(elem, "width");
        float h = GetFloatAttr(elem, "height");

        if (fill.HasValue)
        {
            ctx.FillColor = fill.Value;
            ctx.DrawRectangle(x, y, x + w, y + h);
        }
        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawRectangle(x, y, x + w, y + h);
        }
    }

    private static void RenderCircle(XmlElement elem, DrawingContext ctx,
        DrawColor? fill, DrawColor? stroke, float strokeWidth)
    {
        float cx = GetFloatAttr(elem, "cx");
        float cy = GetFloatAttr(elem, "cy");
        float r = GetFloatAttr(elem, "r");

        if (fill.HasValue)
        {
            ctx.FillColor = fill.Value;
            ctx.DrawCircle(cx, cy, r);
        }
        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawCircle(cx, cy, r);
        }
    }

    private static void RenderEllipse(XmlElement elem, DrawingContext ctx,
        DrawColor? fill, DrawColor? stroke, float strokeWidth)
    {
        float cx = GetFloatAttr(elem, "cx");
        float cy = GetFloatAttr(elem, "cy");
        float rx = GetFloatAttr(elem, "rx");
        float ry = GetFloatAttr(elem, "ry");

        if (fill.HasValue)
        {
            ctx.FillColor = fill.Value;
            ctx.DrawEllipse(cx, cy, rx, ry);
        }
        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawEllipse(cx, cy, rx, ry);
        }
    }

    private static void RenderLine(XmlElement elem, DrawingContext ctx,
        DrawColor? stroke, float strokeWidth)
    {
        float x1 = GetFloatAttr(elem, "x1");
        float y1 = GetFloatAttr(elem, "y1");
        float x2 = GetFloatAttr(elem, "x2");
        float y2 = GetFloatAttr(elem, "y2");

        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawLine(x1, y1, x2, y2);
        }
    }

    private static void RenderPoly(XmlElement elem, DrawingContext ctx,
        DrawColor? fill, DrawColor? stroke, float strokeWidth, bool isPolygon)
    {
        string points = elem.GetAttribute("points") ?? "";
        var pts = ParsePointList(points);
        if (pts.Length == 0)
        {
            return;
        }

        // Build SVG path string
        var sb = new StringBuilder();
        sb.Append($"M {pts[0].X.ToString(CultureInfo.InvariantCulture)} {pts[0].Y.ToString(CultureInfo.InvariantCulture)}");
        for (int i = 1;i < pts.Length;i++)
        {
            sb.Append($" L {pts[i].X.ToString(CultureInfo.InvariantCulture)} {pts[i].Y.ToString(CultureInfo.InvariantCulture)}");
        }

        if (isPolygon)
        {
            sb.Append(" Z");
        }

        if (fill.HasValue && isPolygon)
        {
            ctx.FillColor = fill.Value;
            ctx.DrawPath(sb.ToString());
        }
        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawPath(sb.ToString());
        }
    }

    private static void RenderPath(XmlElement elem, DrawingContext ctx,
        DrawColor? fill, DrawColor? stroke, float strokeWidth)
    {
        string d = elem.GetAttribute("d") ?? "";
        if (string.IsNullOrWhiteSpace(d))
        {
            return;
        }

        if (fill.HasValue)
        {
            ctx.FillColor = fill.Value;
            ctx.DrawPath(d);
        }
        if (stroke.HasValue && strokeWidth > 0)
        {
            ctx.StrokeColor = stroke.Value;
            ctx.StrokeWidth = strokeWidth;
            ctx.DrawPath(d);
        }
    }

    #endregion

    #region Style parsing

    private static void ParseStyle(XmlElement elem,
        out DrawColor? fill, out DrawColor? stroke, out float strokeWidth, out float opacity)
    {
        fill = null;
        stroke = null;
        strokeWidth = 0;
        opacity = 1f;

        // Parse inline style attribute
        string style = elem.GetAttribute("style") ?? "";
        var styleProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(style))
        {
            foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colonIdx = part.IndexOf(':');
                if (colonIdx > 0)
                {
                    string key = part[..colonIdx].Trim();
                    string val = part[(colonIdx + 1)..].Trim();
                    styleProps[key] = val;
                }
            }
        }

        // Check attributes (style overrides attributes)
        string? fillStr = GetStyleOrAttr(elem, styleProps, "fill", null);
        string? strokeStr = GetStyleOrAttr(elem, styleProps, "stroke", null);
        string? swStr = GetStyleOrAttr(elem, styleProps, "stroke-width", null);
        string? opStr = GetStyleOrAttr(elem, styleProps, "opacity", null);

        if (fillStr != null && fillStr != "none")
        {
            fill = ParseColor(fillStr);
        }
        else if (fillStr == null)
        {
            fill = new DrawColor(0, 0, 0, 255); // default SVG fill = black
        }

        if (strokeStr != null && strokeStr != "none")
        {
            stroke = ParseColor(strokeStr);
        }

        if (swStr != null)
        {
            strokeWidth = ParseFloat(swStr);
        }

        if (opStr != null)
        {
            opacity = ParseFloat(opStr);
        }
    }

    private static string? GetStyleOrAttr(XmlElement elem,
        Dictionary<string, string> styleProps, string name, string? defaultVal)
    {
        if (styleProps.TryGetValue(name, out var val))
        {
            return val;
        }

        string attr = elem.GetAttribute(name);
        return string.IsNullOrEmpty(attr) ? defaultVal : attr;
    }

    private static DrawColor? ParseColor(string color)
    {
        color = color.Trim().ToLowerInvariant();

        // Named colors (common subset)
        switch (color)
        {
            case "black":
                return new DrawColor(0, 0, 0, 255);
            case "white":
                return new DrawColor(255, 255, 255, 255);
            case "red":
                return new DrawColor(255, 0, 0, 255);
            case "green":
                return new DrawColor(0, 128, 0, 255);
            case "blue":
                return new DrawColor(0, 0, 255, 255);
            case "yellow":
                return new DrawColor(255, 255, 0, 255);
            case "cyan":
                return new DrawColor(0, 255, 255, 255);
            case "magenta":
                return new DrawColor(255, 0, 255, 255);
            case "gray": case "grey":
                return new DrawColor(128, 128, 128, 255);
            case "orange":
                return new DrawColor(255, 165, 0, 255);
            case "purple":
                return new DrawColor(128, 0, 128, 255);
            case "lime":
                return new DrawColor(0, 255, 0, 255);
            case "navy":
                return new DrawColor(0, 0, 128, 255);
            case "transparent":
                return new DrawColor(0, 0, 0, 0);
        }

        // Hex color
        if (color.StartsWith('#'))
        {
            string hex = color[1..];
            if (hex.Length == 3)
            {
                byte r = (byte)(Convert.ToByte(hex[..1], 16) * 17);
                byte g = (byte)(Convert.ToByte(hex[1..2], 16) * 17);
                byte b = (byte)(Convert.ToByte(hex[2..3], 16) * 17);
                return new DrawColor(r, g, b, 255);
            }
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return new DrawColor(r, g, b, 255);
            }
        }

        // rgb(r,g,b)
        if (color.StartsWith("rgb(") && color.EndsWith(')'))
        {
            var parts = color[4..^1].Split(',');
            if (parts.Length == 3)
            {
                byte r = (byte)ParseFloat(parts[0]);
                byte g = (byte)ParseFloat(parts[1]);
                byte b = (byte)ParseFloat(parts[2]);
                return new DrawColor(r, g, b, 255);
            }
        }

        return null;
    }

    #endregion

    #region Transform parsing

    private static void ApplyTransform(DrawingContext ctx, string transform)
    {
        if (string.IsNullOrEmpty(transform))
        {
            return;
        }

        int pos = 0;
        while (pos < transform.Length)
        {
            SkipWhitespace(transform, ref pos);
            if (pos >= transform.Length)
            {
                break;
            }

            int nameStart = pos;
            while (pos < transform.Length && transform[pos] != '(')
            {
                pos++;
            }

            string name = transform[nameStart..pos].Trim().ToLowerInvariant();
            if (pos >= transform.Length)
            {
                break;
            }

            pos++; // skip '('

            int argsStart = pos;
            while (pos < transform.Length && transform[pos] != ')')
            {
                pos++;
            }

            string argsStr = transform[argsStart..pos];
            if (pos < transform.Length)
            {
                pos++; // skip ')'
            }

            var args = argsStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => ParseFloat(s.Trim())).ToArray();

            switch (name)
            {
                case "translate" when args.Length >= 1:
                    ctx.Translate(args[0], args.Length >= 2 ? args[1] : 0);
                    break;
                case "scale" when args.Length >= 1:
                    ctx.Scale(args[0], args.Length >= 2 ? args[1] : args[0]);
                    break;
                case "rotate" when args.Length >= 1:
                    if (args.Length >= 3)
                    {
                        ctx.Translate(args[1], args[2]);
                        ctx.Rotate(args[0]);
                        ctx.Translate(-args[1], -args[2]);
                    }
                    else
                    {
                        ctx.Rotate(args[0]);
                    }
                    break;
            }
        }
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    #endregion

    #region Helpers

    private static XmlElement? FindSvgElement(XmlDocument doc)
    {
        if (doc.DocumentElement?.LocalName == "svg")
        {
            return doc.DocumentElement;
        }

        foreach (XmlNode node in doc.ChildNodes)
        {
            if (node is XmlElement elem && elem.LocalName == "svg")
            {
                return elem;
            }
        }
        return null;
    }

    private static int ParseDimension(string? value, int defaultVal)
    {
        if (string.IsNullOrEmpty(value))
        {
            return defaultVal;
        }
        // Strip units (px, em, etc.)
        var numStr = new string(value.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return int.TryParse(numStr, CultureInfo.InvariantCulture, out int v) ? v : defaultVal;
    }

    private static float ParseFloat(string value)
    {
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : 0f;
    }

    private static float GetFloatAttr(XmlElement elem, string name)
    {
        string val = elem.GetAttribute(name);
        return string.IsNullOrEmpty(val) ? 0f : ParseFloat(val);
    }

    private static PointD[] ParsePointList(string points)
    {
        var parts = points.Split(new[] { ' ', ',', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries);
        var result = new List<PointD>();
        for (int i = 0;i + 1 < parts.Length;i += 2)
        {
            float x = ParseFloat(parts[i]);
            float y = ParseFloat(parts[i + 1]);
            result.Add(new PointD(x, y));
        }
        return result.ToArray();
    }

    #endregion
}

