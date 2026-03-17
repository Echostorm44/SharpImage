// CameraRawCoder — Public API for decoding camera raw formats.
// Routes to per-format parsers, applies shared demosaic + processing pipeline.
// Decode-only for proprietary formats; decode+encode for DNG.

using SharpImage.Core;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>
/// Decodes camera raw image formats (CR2, CR3, NEF, ARW, DNG, ORF, RW2, RAF, PEF, SRW, and 21 others).
/// All proprietary formats are decode-only. DNG supports both decode and encode.
/// </summary>
public static class CameraRawCoder
{
    /// <summary>
    /// Detects whether data begins with a known camera raw format signature.
    /// Must be called BEFORE TiffCoder.CanDecode() since many raw formats are TIFF-based.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        return DetectRawFormat(data) != CameraRawFormat.Unknown;
    }

    /// <summary>
    /// Detects the specific camera raw format from file header bytes.
    /// </summary>
    public static CameraRawFormat DetectRawFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return CameraRawFormat.Unknown;

        // ── CR2: II\x2A\x00 + "CR\x02" at offset 8 ──
        if (data[0] == 'I' && data[1] == 'I' && data[2] == 0x2A && data[3] == 0x00
            && data.Length > 10 && data[8] == 'C' && data[9] == 'R' && data[10] == 0x02)
            return CameraRawFormat.CR2;

        // ── CR3: ISO BMFF with "ftypcrx " ──
        if (data.Length > 12 && data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p'
            && data[8] == 'c' && data[9] == 'r' && data[10] == 'x')
            return CameraRawFormat.CR3;

        // ── RAF: "FUJIFILMCCD-RAW" ──
        if (data.Length > 16 && data[0] == 'F' && data[1] == 'U' && data[2] == 'J' && data[3] == 'I'
            && data[4] == 'F' && data[5] == 'I' && data[6] == 'L' && data[7] == 'M')
            return CameraRawFormat.RAF;

        // ── ORF: II + 0x4F52 or MM + 0x524F (Olympus magic) ──
        if (data[0] == 'I' && data[1] == 'I')
        {
            int magic = data[2] | (data[3] << 8);
            if (magic == 0x4F52) return CameraRawFormat.ORF; // "RO" little-endian
        }
        else if (data[0] == 'M' && data[1] == 'M')
        {
            int magic = (data[2] << 8) | data[3];
            if (magic == 0x4F52) return CameraRawFormat.ORF;
        }

        // ── RW2: II + 0x0055 (Panasonic magic) ──
        if (data[0] == 'I' && data[1] == 'I')
        {
            int magic = data[2] | (data[3] << 8);
            if (magic == 0x0055) return CameraRawFormat.RW2;
        }

        // ── MRW: \x00MRM (Minolta) ──
        if (data[0] == 0x00 && data[1] == 'M' && data[2] == 'R' && data[3] == 'M')
            return CameraRawFormat.MRW;

        // ── X3F: FOVb (Sigma/Foveon) ──
        if (data[0] == 'F' && data[1] == 'O' && data[2] == 'V' && data[3] == 'b')
            return CameraRawFormat.X3F;

        // ── CRW: II\x1A\x00\x00\x00HEAP (Canon CIFF) ──
        if (data.Length > 14 && data[0] == 'I' && data[1] == 'I' && data[2] == 0x1A && data[3] == 0x00
            && data[6] == 'H' && data[7] == 'E' && data[8] == 'A' && data[9] == 'P')
            return CameraRawFormat.CRW;

        // ── TIFF-based formats: Need deeper inspection for DNG, NEF, PEF, ARW, SRW, etc. ──
        bool isTiff = false;
        bool bigEndian = false;

        if (data[0] == 'I' && data[1] == 'I' && data[2] == 0x2A && data[3] == 0x00)
        { isTiff = true; bigEndian = false; }
        else if (data[0] == 'M' && data[1] == 'M' && data[2] == 0x00 && data[3] == 0x2A)
        { isTiff = true; bigEndian = true; }

        if (!isTiff) return CameraRawFormat.Unknown;

        // For TIFF-based detection, we need to check IFD tags
        // This requires reading the full data (ReadOnlySpan can't do random access efficiently for IFD parsing)
        // So we do a quick-scan for known tag patterns
        return DetectTiffBasedRaw(data, bigEndian);
    }

    /// <summary>
    /// Detects TIFF-based camera raw formats by scanning for format-specific tags.
    /// </summary>
    private static CameraRawFormat DetectTiffBasedRaw(ReadOnlySpan<byte> data, bool bigEndian)
    {
        // Get first IFD offset
        uint ifdOffset = bigEndian
            ? (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7])
            : (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));

        if (ifdOffset + 2 >= data.Length) return CameraRawFormat.Unknown;

        int entryCount = bigEndian
            ? (data[(int)ifdOffset] << 8) | data[(int)ifdOffset + 1]
            : data[(int)ifdOffset] | (data[(int)ifdOffset + 1] << 8);

        if (entryCount <= 0 || entryCount > 500) return CameraRawFormat.Unknown;

        int pos = (int)ifdOffset + 2;
        bool hasDngVersion = false;
        string make = "";

        for (int i = 0; i < entryCount && pos + 12 <= data.Length; i++, pos += 12)
        {
            ushort tag = bigEndian
                ? (ushort)((data[pos] << 8) | data[pos + 1])
                : (ushort)(data[pos] | (data[pos + 1] << 8));

            // DNG Version tag (50706)
            if (tag == 50706) hasDngVersion = true;

            // Make tag (271) — read the string value
            if (tag == 271 && make.Length == 0)
            {
                ushort type = bigEndian
                    ? (ushort)((data[pos + 2] << 8) | data[pos + 3])
                    : (ushort)(data[pos + 2] | (data[pos + 3] << 8));
                int count = bigEndian
                    ? (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7]
                    : data[pos + 4] | (data[pos + 5] << 8) | (data[pos + 6] << 16) | (data[pos + 7] << 24);

                if (type == 2 && count > 0 && count < 64) // ASCII type
                {
                    int valueOffset;
                    if (count <= 4)
                        valueOffset = pos + 8;
                    else
                    {
                        valueOffset = bigEndian
                            ? (data[pos + 8] << 24) | (data[pos + 9] << 16) | (data[pos + 10] << 8) | data[pos + 11]
                            : data[pos + 8] | (data[pos + 9] << 8) | (data[pos + 10] << 16) | (data[pos + 11] << 24);
                    }

                    if (valueOffset >= 0 && valueOffset + count <= data.Length)
                    {
                        var makeBytes = data.Slice(valueOffset, Math.Min(count, 32));
                        int nullPos = makeBytes.IndexOf((byte)0);
                        int len = nullPos >= 0 ? nullPos : makeBytes.Length;
                        make = System.Text.Encoding.ASCII.GetString(data.Slice(valueOffset, len));
                    }
                }
            }
        }

        // DNG has its own version tag — most reliable detection
        if (hasDngVersion) return CameraRawFormat.DNG;

        // Detect by manufacturer
        make = make.Trim();
        if (make.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.NEF;
        if (make.StartsWith("SONY", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.ARW;
        if (make.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) ||
            make.StartsWith("RICOH", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.PEF;
        if (make.StartsWith("SAMSUNG", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.SRW;
        if (make.StartsWith("Hasselblad", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.ThreeFR;
        if (make.StartsWith("Phase One", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.IIQ;
        if (make.StartsWith("Leaf", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.MOS;
        if (make.StartsWith("Kodak", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.DCR;
        if (make.StartsWith("Mamiya", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.MEF;
        if (make.StartsWith("EPSON", StringComparison.OrdinalIgnoreCase)) return CameraRawFormat.ERF;

        return CameraRawFormat.Unknown;
    }

    /// <summary>
    /// Decodes a camera raw file to an ImageFrame using the specified options.
    /// </summary>
    public static ImageFrame Decode(byte[] data, CameraRawDecodeOptions? options = null)
    {
        var opts = options ?? CameraRawDecodeOptions.Default;
        var format = DetectRawFormat(data);
        if (format == CameraRawFormat.Unknown)
            throw new InvalidDataException("Cannot detect camera raw format from file header");

        return DecodeFormat(data, format, opts);
    }

    /// <summary>
    /// Decodes a camera raw file of a known format.
    /// </summary>
    public static ImageFrame DecodeFormat(byte[] data, CameraRawFormat format, CameraRawDecodeOptions? options = null)
    {
        var opts = options ?? CameraRawDecodeOptions.Default;

        // Parse format-specific structure → RawSensorData
        RawSensorData sensorData = ParseFormat(data, format);

        // Apply processing pipeline: demosaic → white balance → color matrix → gamma → orientation
        return ProcessRawData(sensorData, opts);
    }

    /// <summary>
    /// Extracts camera raw metadata without performing the expensive demosaic/processing pipeline.
    /// Useful for displaying file info (make, model, sensor dimensions, Bayer pattern, etc.).
    /// </summary>
    public static CameraRawMetadata GetMetadata(byte[] data)
    {
        var format = DetectRawFormat(data);
        if (format == CameraRawFormat.Unknown)
            throw new InvalidDataException("Cannot detect camera raw format from file header");
        var sensorData = ParseFormat(data, format);
        return sensorData.Metadata;
    }

    /// <summary>
    /// Encodes an ImageFrame as DNG. Only DNG encoding is supported.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        var metadata = new CameraRawMetadata
        {
            Format = CameraRawFormat.DNG,
            SensorWidth = (int)image.Columns,
            SensorHeight = (int)image.Rows,
            BitsPerSample = 16,
            BayerPattern = BayerPattern.RGGB,
            Orientation = 1
        };
        return DngParser.Encode(image, metadata);
    }

    // ── Format Routing ──────────────────────────────────────────────────────

    private static RawSensorData ParseFormat(byte[] data, CameraRawFormat format)
    {
        return format switch
        {
            CameraRawFormat.DNG => DngParser.Parse(data),
            CameraRawFormat.CR2 => Cr2Parser.Parse(data),
            CameraRawFormat.CR3 => Cr3Parser.Parse(data),
            CameraRawFormat.NEF or CameraRawFormat.NRW => NefParser.Parse(data),
            CameraRawFormat.ARW or CameraRawFormat.SR2 or CameraRawFormat.SRF => ArwParser.Parse(data),
            CameraRawFormat.ORF => OrfParser.Parse(data),
            CameraRawFormat.RW2 => Rw2Parser.Parse(data),
            CameraRawFormat.RAF => RafParser.Parse(data),
            CameraRawFormat.PEF => PefParser.Parse(data),
            CameraRawFormat.SRW => SrwParser.Parse(data),
            // All other formats through the generic parser
            _ => GenericRawParser.Parse(data, format)
        };
    }

    // ── Processing Pipeline ─────────────────────────────────────────────────

    private static ImageFrame ProcessRawData(RawSensorData sensorData, CameraRawDecodeOptions options)
    {
        var metadata = sensorData.Metadata;
        int width = sensorData.Width;
        int height = sensorData.Height;

        if (sensorData.RawPixels.Length == 0 || width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid sensor data: {width}×{height}, {sensorData.RawPixels.Length} pixels");

        // 0. Compute black level from masked (optically black) border pixels if not provided.
        // Many cameras have masked border pixels outside the active area that represent
        // the sensor's true black level. We sample from left border OR top border columns/rows.
        if (metadata.BlackLevel == 0)
        {
            long sum = 0;
            int count = 0;

            // Try left border columns first (most common for Bayer sensors)
            if (metadata.ActiveAreaLeft > 0)
            {
                int maskedCols = Math.Min(metadata.ActiveAreaLeft, width);
                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * width;
                    for (int x = 0; x < maskedCols; x++)
                    {
                        sum += sensorData.RawPixels[rowStart + x];
                        count++;
                    }
                }
            }

            // Also try top border rows if left border had insufficient pixels
            if (count < 100 && metadata.ActiveAreaTop > 0)
            {
                int maskedRows = Math.Min(metadata.ActiveAreaTop, height);
                for (int y = 0; y < maskedRows; y++)
                {
                    int rowStart = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        sum += sensorData.RawPixels[rowStart + x];
                        count++;
                    }
                }
            }

            if (count > 100)
            {
                metadata.BlackLevel = (int)(sum / count);
                sensorData.Metadata = metadata;
            }
        }

        // 1a. Auto-detect masked/garbage edge columns when no explicit active area is set.
        // Many raw sensors have optically masked columns on the left and saturated/garbage
        // columns on the right that aren't described by any metadata tag.
        if (metadata.ActiveAreaLeft == 0 && metadata.ActiveAreaWidth >= width)
        {
            int bl = metadata.BlackLevel > 0 ? metadata.BlackLevel : 64;
            int wl = metadata.WhiteLevel > 0 ? metadata.WhiteLevel : 65535;
            int sampleRows = Math.Min(200, height);
            int rowStep = Math.Max(1, height / sampleRows);

            // Scan from left: find first column whose mean exceeds blackLevel + 10% of range
            int darkThreshold = bl + (wl - bl) / 10;
            int detectedLeft = 0;
            for (int x = 0; x < Math.Min(40, width); x++)
            {
                long colSum = 0;
                int cnt = 0;
                for (int y = 0; y < height; y += rowStep)
                {
                    colSum += sensorData.RawPixels[y * width + x];
                    cnt++;
                }
                if (colSum / cnt > darkThreshold) break;
                detectedLeft = x + 1;
            }

            // Scan from right: find last column that isn't saturated/garbage.
            // Garbage columns often have mean > 4× the center mean.
            long centerSum = 0;
            int centerCnt = 0;
            int cy = height / 2, cx = width / 2;
            for (int dy = -50; dy < 50 && cy + dy >= 0 && cy + dy < height; dy++)
                for (int dx = -50; dx < 50 && cx + dx >= 0 && cx + dx < width; dx++)
                {
                    centerSum += sensorData.RawPixels[(cy + dy) * width + (cx + dx)];
                    centerCnt++;
                }
            long centerMean = centerCnt > 0 ? centerSum / centerCnt : wl / 2;
            long garbageThreshold = Math.Max(centerMean * 4, (long)(wl * 0.3));

            int detectedRight = 0;
            for (int x = width - 1; x >= width - 60 && x >= 0; x--)
            {
                long colSum = 0;
                int cnt = 0;
                for (int y = 0; y < height; y += rowStep)
                {
                    colSum += sensorData.RawPixels[y * width + x];
                    cnt++;
                }
                if (colSum / cnt < garbageThreshold) break;
                detectedRight++;
            }

            if (detectedLeft > 0 || detectedRight > 0)
            {
                int cfaPeriod = metadata.CfaType == CfaType.XTrans ? 6 : 2;
                detectedLeft = ((detectedLeft + cfaPeriod - 1) / cfaPeriod) * cfaPeriod;
                detectedRight = (detectedRight / cfaPeriod) * cfaPeriod;
                if (detectedLeft > 0)
                    metadata.ActiveAreaLeft = detectedLeft;
                if (detectedRight > 0)
                    metadata.ActiveAreaWidth = width - detectedLeft - detectedRight;
                sensorData.Metadata = metadata;
            }
        }

        // 1b. Crop to active area if specified
        if (metadata.ActiveAreaLeft > 0 || metadata.ActiveAreaTop > 0 ||
            (metadata.ActiveAreaWidth > 0 && metadata.ActiveAreaWidth < width) ||
            (metadata.ActiveAreaHeight > 0 && metadata.ActiveAreaHeight < height))
        {
            int aaLeft = Math.Max(0, metadata.ActiveAreaLeft);
            int aaTop = Math.Max(0, metadata.ActiveAreaTop);

            // Align crop to CFA pattern period to preserve correct color assignment.
            // X-Trans uses a 6×6 pattern; Bayer uses 2×2. Misalignment scrambles colors.
            int cfaPeriod = metadata.CfaType == CfaType.XTrans ? 6 : 2;
            aaLeft = (aaLeft / cfaPeriod) * cfaPeriod;
            aaTop = (aaTop / cfaPeriod) * cfaPeriod;

            int aaWidth = metadata.ActiveAreaWidth > 0 ? metadata.ActiveAreaWidth : width - aaLeft;
            int aaHeight = metadata.ActiveAreaHeight > 0 ? metadata.ActiveAreaHeight : height - aaTop;
            aaWidth = Math.Min(aaWidth, width - aaLeft);
            aaHeight = Math.Min(aaHeight, height - aaTop);

            if (aaWidth < width || aaHeight < height)
            {
                var cropped = new ushort[aaWidth * aaHeight];
                for (int y = 0; y < aaHeight; y++)
                    Array.Copy(sensorData.RawPixels, (aaTop + y) * width + aaLeft, cropped, y * aaWidth, aaWidth);

                // When rounding the crop offset changed it from the original ActiveArea values,
                // the pre-computed X-Trans CFA is misaligned. Recompute for the actual crop position.
                byte[,]? correctedCfa = sensorData.XTransCfaEffective;
                if (correctedCfa != null &&
                    (aaTop != metadata.ActiveAreaTop || aaLeft != metadata.ActiveAreaLeft))
                {
                    // effectiveCfa was shifted for (origTop, origLeft). We cropped at (aaTop, aaLeft).
                    // Adjust by the modular difference so the CFA aligns with the actual crop.
                    int deltaRow = (aaTop - metadata.ActiveAreaTop + 600) % 6;
                    int deltaCol = (aaLeft - metadata.ActiveAreaLeft + 600) % 6;
                    correctedCfa = new byte[6, 6];
                    var oldCfa = sensorData.XTransCfaEffective;
                    for (int r = 0; r < 6; r++)
                        for (int c = 0; c < 6; c++)
                            correctedCfa[r, c] = oldCfa![(r + deltaRow) % 6, (c + deltaCol) % 6];
                }

                sensorData = new RawSensorData
                {
                    RawPixels = cropped,
                    Width = aaWidth,
                    Height = aaHeight,
                    Metadata = metadata,
                    XTransCfaEffective = correctedCfa
                };
            }
        }

        // 2. Scale to full 16-bit range (includes linearization table if present)
        CameraRawProcessor.ScaleToFullRange(ref sensorData);

        // 2b. Apply WB at Bayer level before demosaic (dcraw's scale_colors behavior).
        // This ensures the demosaic algorithm interpolates correctly balanced values and
        // produces the same highlight clipping as dcraw/libraw, which is critical for
        // the auto-brightness histogram to compute the correct multiplier.
        if (sensorData.Metadata.CfaType == CfaType.Bayer &&
            CameraRawProcessor.TryResolveWhiteBalanceFromMetadata(in metadata, in options,
                out double bayerWbR, out double bayerWbG, out double bayerWbB))
        {
            CameraRawProcessor.ApplyWhiteBalanceToBayer(ref sensorData, bayerWbR, bayerWbG, bayerWbB);
            metadata.WbAppliedAtBayerLevel = true;
        }

        // 3. Demosaic
        ImageFrame frame;
        if (metadata.CfaType == CfaType.XTrans)
        {
            frame = BayerDemosaic.DemosaicXTrans(in sensorData);
        }
        else if (metadata.CfaType == CfaType.Foveon)
        {
            frame = CreateFrameFromRawRgb(sensorData.RawPixels, sensorData.Width, sensorData.Height);
        }
        else
        {
            frame = BayerDemosaic.Demosaic(in sensorData, options.Algorithm);
        }

        // 4.Look up camera color matrix for non-DNG formats that lack embedded matrices.
        // dcraw's adobe_coeff matrices are calibrated for D65 illuminant (not D50 like DNG spec).
        // Store as ColorMatrix1 with ColorMatrixIsD65 flag so the processor uses the correct
        // D65 XYZ→sRGB matrix instead of the D50-adapted one used for DNG-embedded matrices.
        if (metadata.ForwardMatrix1 is null or { Length: 0 } &&
            metadata.ColorMatrix1 is null or { Length: 0 } &&
            metadata.Format != CameraRawFormat.DNG)
        {
            var matrix = CameraRawParserUtils.LookupCameraColorMatrix(metadata.Make, metadata.Model);
            if (matrix is not null)
            {
                metadata.ColorMatrix1 = matrix;
                metadata.ColorMatrixIsD65 = true;
            }
        }

        // 5. Apply full processing pipeline (WB, color matrix, highlight recovery, gamma)
        CameraRawProcessor.Process(frame, in metadata, in options);

        // 5. Apply orientation
        if (metadata.Orientation > 1)
        {
            frame = CameraRawProcessor.ApplyOrientation(frame, metadata.Orientation);
        }

        return frame;
    }

    private static ImageFrame CreateFrameFromRawRgb(ushort[] pixels, int width, int height)
    {
        // For Foveon or pre-demosaiced data, create a 3-channel frame
        var frame = new ImageFrame();
        frame.Initialize((uint)width, (uint)height, ColorspaceType.SRGB, false);

        int pixelsPerChannel = width * height;
        for (int y = 0; y < height; y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int srcIdx = y * width + x;
                int dstIdx = x * 3;
                // Map raw pixel as grayscale if single channel, or interleave if 3-channel
                ushort v = srcIdx < pixels.Length ? pixels[srcIdx] : (ushort)0;
                row[dstIdx] = v;
                row[dstIdx + 1] = v;
                row[dstIdx + 2] = v;
            }
        }

        return frame;
    }

    // ── Format-Specific Extension Detection ─────────────────────────────────

    /// <summary>
    /// Returns the ImageFileFormat for a camera raw extension, or Unknown if not a raw extension.
    /// </summary>
    internal static ImageFileFormat DetectCameraRawFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
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
}
