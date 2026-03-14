// FITS (Flexible Image Transport System) format coder — read and write.
// Used in astronomy. 80-char keyword records in 2880-byte blocks.
// BITPIX determines data type: 8=byte, 16=short, 32=int, -32=float, -64=double.
// BSCALE and BZERO provide linear transformation for data values.

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpImage.Formats;

public static class FitsCoder
{
    private const int RecordLength = 80;
    private const int BlockSize = 2880;

    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
        {
            return false;
        }

        string header = Encoding.ASCII.GetString(data[..30]);
        return header.StartsWith("SIMPLE  =");
    }

    public static ImageFrame Decode(byte[] data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid FITS file");
        }

        var keywords = new Dictionary<string, string>();
        int pos = 0;

        // Parse header records
        while (pos + RecordLength <= data.Length)
        {
            string record = Encoding.ASCII.GetString(data, pos, RecordLength);
            pos += RecordLength;

            string key = record[..8].Trim();
            if (key == "END")
            {
                break;
            }

            if (record.Length > 10 && record[8] == '=')
            {
                string value = record[10..].Trim();
                // Remove comments (after /)
                int slashIdx = value.IndexOf('/');
                if (slashIdx >= 0)
                {
                    value = value[..slashIdx].Trim();
                }
                // Remove quotes for string values
                value = value.Trim().Trim('\'').Trim();
                keywords[key] = value;
            }
        }

        // Pad to block boundary
        pos = ((pos + BlockSize - 1) / BlockSize) * BlockSize;

        if (!keywords.TryGetValue("NAXIS", out string? naxisStr) || int.Parse(naxisStr) < 2)
        {
            throw new InvalidDataException("FITS image must have at least 2 axes");
        }

        int width = int.Parse(keywords["NAXIS1"]);
        int height = int.Parse(keywords["NAXIS2"]);
        int bitPix = int.Parse(keywords["BITPIX"]);
        int naxis = int.Parse(naxisStr);
        int naxis3 = naxis >= 3 && keywords.TryGetValue("NAXIS3", out string? n3) ? int.Parse(n3) : 1;

        double bscale = keywords.TryGetValue("BSCALE", out string? bs)
            ? double.Parse(bs, CultureInfo.InvariantCulture) : 1.0;
        double bzero = keywords.TryGetValue("BZERO", out string? bz)
            ? double.Parse(bz, CultureInfo.InvariantCulture) : 0.0;

        int samplesPerPixel = Math.Min(naxis3, 3);
        bool hasAlpha = naxis3 >= 4;

        var frame = new ImageFrame();
        if (samplesPerPixel == 1)
        {
            frame.Initialize(width, height, ColorspaceType.SRGB, false);
        }
        else
        {
            frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        }

        int channels = frame.NumberOfChannels;

        // Read pixel data (FITS stores bottom-to-top, plane-by-plane)
        int bytesPerSample = Math.Abs(bitPix) / 8;
        int planeSize = width * height * bytesPerSample;

        for (int plane = 0;plane < Math.Min(naxis3, channels);plane++)
        {
            int planeOffset = pos + plane * planeSize;
            for (int y = 0;y < height;y++)
            {
                // FITS is bottom-to-top
                int fitsY = height - 1 - y;
                var row = frame.GetPixelRowForWrite(y);

                for (int x = 0;x < width;x++)
                {
                    int samplePos = planeOffset + (fitsY * width + x) * bytesPerSample;
                    if (samplePos + bytesPerSample > data.Length)
                    {
                        continue;
                    }

                    double rawValue = bitPix switch
                    {
                        8 => data[samplePos],
                        16 => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(samplePos)),
                        32 => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(samplePos)),
                        -32 => BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(samplePos))),
                        -64 => BitConverter.Int64BitsToDouble(
                            BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(samplePos))),
                        _ => 0
                    };

                    double physicalValue = bscale * rawValue + bzero;

                    ushort quantum = bitPix switch
                    {
                        8 => Quantum.ScaleFromByte((byte)Math.Clamp(physicalValue, 0, 255)),
                        16 => (ushort)Math.Clamp(physicalValue, 0, Quantum.MaxValue),
                        32 => (ushort)Math.Clamp(physicalValue * 65535.0 / int.MaxValue, 0, Quantum.MaxValue),
                        _ => (ushort)Math.Clamp(physicalValue * Quantum.MaxValue, 0, Quantum.MaxValue)
                    };

                    int channel = samplesPerPixel == 1 ? 0 : plane;
                    int offset = x * channels + channel;
                    row[offset] = quantum;

                    // For grayscale, replicate to all channels
                    if (samplesPerPixel == 1 && channels >= 3)
                    {
                        row[x * channels + 1] = quantum;
                        row[x * channels + 2] = quantum;
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

        // Write as 16-bit FITS (BITPIX=16, BZERO=32768 for unsigned)
        int naxis3 = imgChannels;
        int bytesPerSample = 2;
        int planeSize = w * h * bytesPerSample;
        int dataSize = planeSize * naxis3;
        int dataPadded = ((dataSize + BlockSize - 1) / BlockSize) * BlockSize;

        var headerRecords = new List<string>();
        AddRecord(headerRecords, "SIMPLE", "T");
        AddRecord(headerRecords, "BITPIX", "16");
        AddRecord(headerRecords, "NAXIS", naxis3 > 1 ? "3" : "2");
        AddRecord(headerRecords, "NAXIS1", w.ToString());
        AddRecord(headerRecords, "NAXIS2", h.ToString());
        if (naxis3 > 1)
        {
            AddRecord(headerRecords, "NAXIS3", naxis3.ToString());
        }

        AddRecord(headerRecords, "BSCALE", "1.0");
        AddRecord(headerRecords, "BZERO", "32768");
        headerRecords.Add("END".PadRight(RecordLength));

        // Pad header to block boundary
        int headerRecordCount = headerRecords.Count;
        int headerBlockRecords = ((headerRecordCount + (BlockSize / RecordLength - 1)) / (BlockSize / RecordLength)) * (BlockSize / RecordLength);
        while (headerRecords.Count < headerBlockRecords)
        {
            headerRecords.Add(new string(' ', RecordLength));
        }

        int headerSize = headerRecords.Count * RecordLength;
        byte[] output = new byte[headerSize + dataPadded];

        // Write header
        int pos = 0;
        foreach (string rec in headerRecords)
        {
            Encoding.ASCII.GetBytes(rec, output.AsSpan(pos));
            pos += RecordLength;
        }

        // Write pixel data (big-endian, bottom-to-top, plane-by-plane)
        // Use BZERO=32768 to store unsigned 16-bit as signed
        for (int plane = 0;plane < naxis3;plane++)
        {
            int planeOffset = headerSize + plane * planeSize;
            for (int y = 0;y < h;y++)
            {
                int fitsY = h - 1 - y;
                var row = image.GetPixelRow(y);

                for (int x = 0;x < w;x++)
                {
                    ushort val = row[x * imgChannels + plane];
                    // Unsigned to signed with BZERO offset
                    short signedVal = (short)(val - 32768);
                    BinaryPrimitives.WriteInt16BigEndian(
                        output.AsSpan(planeOffset + (fitsY * w + x) * 2), signedVal);
                }
            }
        }

        return output;
    }

    private static void AddRecord(List<string> records, string keyword, string value)
    {
        string rec = $"{keyword,-8}= {value,20}";
        records.Add(rec.PadRight(RecordLength));
    }
}
