// PDF 1.4 image writer. Produces valid PDF documents containing one or more
// images, each on its own page.  Supports Deflate (lossless) and JPEG (lossy)
// stream compression, grayscale / RGB colour spaces, and alpha via soft mask.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>
/// Compression method used for image data inside the PDF.
/// </summary>
public enum PdfCompression
{
    /// <summary>Lossless Deflate compression (FlateDecode).</summary>
    Deflate,

    /// <summary>Lossy JPEG compression (DCTDecode).</summary>
    Jpeg
}

/// <summary>
/// Options that control PDF generation.
/// </summary>
public sealed class PdfWriteOptions
{
    /// <summary>Image stream compression method. Default is Deflate.</summary>
    public PdfCompression Compression { get; set; } = PdfCompression.Deflate;

    /// <summary>JPEG quality (1–100) when Compression is Jpeg. Default is 85.</summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>Resolution used to compute page dimensions. Default is 72 DPI (1 pixel = 1 point).</summary>
    public double Dpi { get; set; } = 72.0;

    /// <summary>PDF document title metadata. Null means no title.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// Write-only PDF coder.  Produces PDF 1.4 documents with embedded images.
/// </summary>
public static class PdfCoder
{
    // ──────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Write a single-image PDF to a file.</summary>
    public static void Write(ImageFrame image, string path, PdfWriteOptions? options = null)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(image, stream, options);
    }

    /// <summary>Write a single-image PDF to a stream.</summary>
    public static void Write(ImageFrame image, Stream stream, PdfWriteOptions? options = null)
    {
        Write([image], stream, options);
    }

    /// <summary>Write a multi-page PDF (one image per page) to a file.</summary>
    public static void Write(ImageFrame[] images, string path, PdfWriteOptions? options = null)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(images, stream, options);
    }

    /// <summary>Write a multi-page PDF (one image per page) to a stream.</summary>
    public static void Write(ImageFrame[] images, Stream stream, PdfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Length == 0)
            throw new ArgumentException("At least one image is required.", nameof(images));

        options ??= new PdfWriteOptions();
        var writer = new PdfWriter(stream, options);
        writer.WritePdf(images);
    }

    /// <summary>Encode a single image to PDF bytes.</summary>
    public static byte[] Encode(ImageFrame image, PdfWriteOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(image, ms, options);
        return ms.ToArray();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internal PDF writer
    // ──────────────────────────────────────────────────────────────────

    private sealed class PdfWriter
    {
        private readonly Stream output;
        private readonly PdfWriteOptions options;
        private readonly List<long> objectOffsets = [];
        private int nextObjectNumber = 1;

        public PdfWriter(Stream output, PdfWriteOptions options)
        {
            this.output = output;
            this.options = options;
        }

        public void WritePdf(ImageFrame[] images)
        {
            // Phase 1 — plan object numbers
            //   1 = Catalog
            //   2 = Pages
            //   3 = Info  (optional, always reserved)
            //   Then per image:  Page, Contents, XObject, (optional SMask)
            int catalogObj = AllocObject();   // 1
            int pagesObj = AllocObject();     // 2
            int infoObj = AllocObject();      // 3

            var pageInfos = new List<PageObjectInfo>(images.Length);
            foreach (var image in images)
            {
                int pageObj = AllocObject();
                int contentsObj = AllocObject();
                int imageObj = AllocObject();
                int maskObj = image.HasAlpha ? AllocObject() : 0;
                pageInfos.Add(new PageObjectInfo(pageObj, contentsObj, imageObj, maskObj));
            }

            // Phase 2 — write
            WriteHeader();
            WriteCatalog(catalogObj, pagesObj);
            WritePages(pagesObj, pageInfos);
            WriteInfo(infoObj);

            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                var pi = pageInfos[i];
                double pageWidth = img.Columns * 72.0 / options.Dpi;
                double pageHeight = img.Rows * 72.0 / options.Dpi;

                WritePage(pi.PageObj, pagesObj, pi.ContentsObj, pi.ImageObj, pageWidth, pageHeight);
                WriteContents(pi.ContentsObj, pageWidth, pageHeight);

                if (pi.MaskObj > 0)
                    WriteImageWithAlpha(pi.ImageObj, pi.MaskObj, img);
                else
                    WriteImage(pi.ImageObj, 0, img);
            }

            WriteXrefAndTrailer(catalogObj, infoObj);
        }

        // ── Object allocation ──

        private int AllocObject()
        {
            int num = nextObjectNumber++;
            // Pad list so index == num
            while (objectOffsets.Count < num)
                objectOffsets.Add(0);
            return num;
        }

        private void BeginObject(int num)
        {
            // Record byte offset for xref
            while (objectOffsets.Count < num)
                objectOffsets.Add(0);
            objectOffsets[num - 1] = output.Position;
            WriteRaw($"{num} 0 obj\n");
        }

        private void EndObject()
        {
            WriteRaw("endobj\n");
        }

        // ── Header ──

        private void WriteHeader()
        {
            WriteRaw("%PDF-1.4\n");
            // Binary comment to mark file as binary (per spec recommendation)
            output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        }

        // ── Catalog ──

        private void WriteCatalog(int catalogObj, int pagesObj)
        {
            BeginObject(catalogObj);
            WriteRaw($"<< /Type /Catalog /Pages {pagesObj} 0 R >>\n");
            EndObject();
        }

        // ── Pages ──

        private void WritePages(int pagesObj, List<PageObjectInfo> pages)
        {
            BeginObject(pagesObj);
            var kids = new StringBuilder();
            kids.Append('[');
            for (int i = 0; i < pages.Count; i++)
            {
                if (i > 0) kids.Append(' ');
                kids.Append(CultureInfo.InvariantCulture, $"{pages[i].PageObj} 0 R");
            }
            kids.Append(']');
            WriteRaw($"<< /Type /Pages /Kids {kids} /Count {pages.Count} >>\n");
            EndObject();
        }

        // ── Info ──

        private void WriteInfo(int infoObj)
        {
            BeginObject(infoObj);
            var sb = new StringBuilder();
            sb.Append("<< /Producer (SharpImage)");
            if (options.Title != null)
                sb.Append(CultureInfo.InvariantCulture, $" /Title ({EscapePdfString(options.Title)})");
            sb.Append(" >>\n");
            WriteRaw(sb.ToString());
            EndObject();
        }

        // ── Page ──

        private void WritePage(int pageObj, int pagesObj, int contentsObj, int imageObj,
            double width, double height)
        {
            BeginObject(pageObj);
            string w = FormatDouble(width);
            string h = FormatDouble(height);
            WriteRaw($"<< /Type /Page /Parent {pagesObj} 0 R " +
                     $"/MediaBox [0 0 {w} {h}] " +
                     $"/Contents {contentsObj} 0 R " +
                     $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> >>\n");
            EndObject();
        }

        // ── Contents (drawing commands) ──

        private void WriteContents(int contentsObj, double width, double height)
        {
            string w = FormatDouble(width);
            string h = FormatDouble(height);
            byte[] commands = Encoding.ASCII.GetBytes($"q\n{w} 0 0 {h} 0 0 cm\n/Im0 Do\nQ\n");

            BeginObject(contentsObj);
            WriteRaw($"<< /Length {commands.Length} >>\nstream\n");
            output.Write(commands);
            WriteRaw("\nendstream\n");
            EndObject();
        }

        // ── Image XObject (no alpha) ──

        private void WriteImage(int imageObj, int maskObj, ImageFrame image)
        {
            bool isGray = image.NumberOfChannels == 1 ||
                          (image.NumberOfChannels == 2 && image.HasAlpha);
            string colorSpace = isGray ? "/DeviceGray" : "/DeviceRGB";
            int colorChannels = isGray ? 1 : 3;

            byte[] imageData = options.Compression == PdfCompression.Jpeg
                ? CompressJpeg(image)
                : CompressDeflatePixels(image, colorChannels);

            string filter = options.Compression == PdfCompression.Jpeg
                ? "/DCTDecode" : "/FlateDecode";

            BeginObject(imageObj);
            var dict = new StringBuilder();
            dict.Append($"<< /Type /XObject /Subtype /Image /Width {image.Columns} /Height {image.Rows} ");
            dict.Append($"/ColorSpace {colorSpace} /BitsPerComponent 8 ");
            dict.Append($"/Filter {filter} /Length {imageData.Length}");
            if (maskObj > 0)
                dict.Append(CultureInfo.InvariantCulture, $" /SMask {maskObj} 0 R");
            dict.Append(" >>\nstream\n");
            WriteRaw(dict.ToString());
            output.Write(imageData);
            WriteRaw("\nendstream\n");
            EndObject();
        }

        // ── Image with alpha (writes image XObject + SMask XObject) ──

        private void WriteImageWithAlpha(int imageObj, int maskObj, ImageFrame image)
        {
            // Write the soft mask (alpha channel) first
            byte[] alphaData = CompressDeflateAlpha(image);

            BeginObject(maskObj);
            WriteRaw($"<< /Type /XObject /Subtype /Image /Width {image.Columns} /Height {image.Rows} " +
                     $"/ColorSpace /DeviceGray /BitsPerComponent 8 " +
                     $"/Filter /FlateDecode /Length {alphaData.Length} >>\nstream\n");
            output.Write(alphaData);
            WriteRaw("\nendstream\n");
            EndObject();

            // Then write the colour image referencing the mask
            WriteImage(imageObj, maskObj, image);
        }

        // ── Pixel compression helpers ──

        private byte[] CompressDeflatePixels(ImageFrame image, int colorChannels)
        {
            int width = (int)image.Columns;
            int height = (int)image.Rows;
            int channels = image.NumberOfChannels;
            int rowBytes = width * colorChannels;
            byte[] rawRow = new byte[rowBytes];

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    var row = image.GetPixelRow(y);
                    if (colorChannels == 1)
                    {
                        for (int x = 0; x < width; x++)
                            rawRow[x] = Quantum.ScaleToByte(row[x * channels]);
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int src = x * channels;
                            int dst = x * 3;
                            rawRow[dst] = Quantum.ScaleToByte(row[src]);
                            rawRow[dst + 1] = Quantum.ScaleToByte(row[src + 1]);
                            rawRow[dst + 2] = Quantum.ScaleToByte(row[src + 2]);
                        }
                    }
                    zlib.Write(rawRow, 0, rowBytes);
                }
            }
            return compressed.ToArray();
        }

        private byte[] CompressDeflateAlpha(ImageFrame image)
        {
            int width = (int)image.Columns;
            int height = (int)image.Rows;
            int channels = image.NumberOfChannels;
            int alphaIndex = channels - 1;
            byte[] rawRow = new byte[width];

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < height; y++)
                {
                    var row = image.GetPixelRow(y);
                    for (int x = 0; x < width; x++)
                        rawRow[x] = Quantum.ScaleToByte(row[x * channels + alphaIndex]);
                    zlib.Write(rawRow, 0, width);
                }
            }
            return compressed.ToArray();
        }

        private byte[] CompressJpeg(ImageFrame image)
        {
            using var ms = new MemoryStream();
            JpegCoder.Write(image, ms, options.JpegQuality);
            return ms.ToArray();
        }

        // ── Cross-reference table and trailer ──

        private void WriteXrefAndTrailer(int catalogObj, int infoObj)
        {
            long xrefOffset = output.Position;
            int objectCount = objectOffsets.Count + 1; // +1 for object 0

            WriteRaw($"xref\n0 {objectCount}\n");
            // Object 0 — free entry
            WriteRaw("0000000000 65535 f \n");
            for (int i = 0; i < objectOffsets.Count; i++)
            {
                WriteRaw(string.Format(CultureInfo.InvariantCulture,
                    "{0:D10} 00000 n \n", objectOffsets[i]));
            }

            WriteRaw("trailer\n");
            WriteRaw($"<< /Size {objectCount} /Root {catalogObj} 0 R /Info {infoObj} 0 R >>\n");
            WriteRaw("startxref\n");
            WriteRaw($"{xrefOffset}\n");
            WriteRaw("%%EOF\n");
        }

        // ── Low-level helpers ──

        private void WriteRaw(string text)
        {
            output.Write(Encoding.ASCII.GetBytes(text));
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string EscapePdfString(string s)
        {
            return s.Replace("\\", "\\\\")
                    .Replace("(", "\\(")
                    .Replace(")", "\\)");
        }
    }

    private readonly record struct PageObjectInfo(int PageObj, int ContentsObj, int ImageObj, int MaskObj);
}
