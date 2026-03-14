namespace SharpImage.Compression;

/// <summary>
/// JPEG standard constants: zigzag scan order, default quantization tables, and default Huffman tables per JPEG spec
/// (ITU-T T.81 / ISO 10918-1).
/// </summary>
public static class JpegTables
{
    /// <summary>
    /// Zigzag scan order mapping: natural order index → zigzag position. Maps the 8x8 block row-major index to the JPEG
    /// zigzag sequence.
    /// </summary>
    public static readonly byte[] ZigzagOrder =[ 0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53, 10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63 ];

    /// <summary>
    /// Natural order: zigzag index → row-major position (inverse of ZigzagOrder).
    /// </summary>
    public static readonly byte[] NaturalOrder =[ 0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63 ];

    /// <summary>
    /// Standard JPEG luminance quantization table (for quality 50). Values in zigzag order. Scale by quality factor for
    /// other quality levels.
    /// </summary>
    public static readonly byte[] LuminanceQuantTable =[ 16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99 ];

    /// <summary>
    /// Standard JPEG chrominance quantization table (for quality 50).
    /// </summary>
    public static readonly byte[] ChrominanceQuantTable =[ 17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99 ];

    /// <summary>
    /// Computes a scaled quantization table for the given quality (1-100). Quality 1 = worst, 50 = standard, 100 = best
    /// (nearly lossless).
    /// </summary>
    public static int[] ScaleQuantTable(ReadOnlySpan<byte> baseTable, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scaleFactor = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var result = new int[64];
        for (int i = 0;i < 64;i++)
        {
            int value = (baseTable[i] * scaleFactor + 50) / 100;
            result[i] = Math.Clamp(value, 1, 255);
        }
        return result;
    }

    // ─── Standard Huffman Tables (JPEG Annex K) ──────────────────

    /// <summary>
    /// DC luminance Huffman code lengths (bits): number of codes of length 1..16.
    /// </summary>
    public static readonly byte[] DcLuminanceBits =[ 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 ];

    /// <summary>
    /// DC luminance Huffman values (categories 0-11).
    /// </summary>
    public static readonly byte[] DcLuminanceValues =[ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 ];

    /// <summary>
    /// DC chrominance Huffman code lengths.
    /// </summary>
    public static readonly byte[] DcChrominanceBits =[ 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 ];

    /// <summary>
    /// DC chrominance Huffman values.
    /// </summary>
    public static readonly byte[] DcChrominanceValues =[ 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 ];

    /// <summary>
    /// AC luminance Huffman code lengths.
    /// </summary>
    public static readonly byte[] AcLuminanceBits =[ 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D ];

    /// <summary>
    /// AC luminance Huffman values (162 entries).
    /// </summary>
    public static readonly byte[] AcLuminanceValues =[ 0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA ];

    /// <summary>
    /// AC chrominance Huffman code lengths.
    /// </summary>
    public static readonly byte[] AcChrominanceBits =[ 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 ];

    /// <summary>
    /// AC chrominance Huffman values (162 entries).
    /// </summary>
    public static readonly byte[] AcChrominanceValues =[ 0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA ];
}

/// <summary>
/// Huffman lookup table for fast JPEG decoding. Built from the standard bits/values specification.
/// </summary>
public sealed class HuffmanTable
{
    // For codes up to MaxLookupBits, use direct lookup
    private const int MaxLookupBits = 8;
    private const int LookupSize = 1 << MaxLookupBits;

    // Fast lookup: indexed by the next MaxLookupBits bits
    // Lower 8 bits = symbol value, upper 8 bits = code length (0 = needs slow path)
    private readonly ushort[] fastLookup = new ushort[LookupSize];

    // Full table for codes > MaxLookupBits
    private readonly int[] maxCode = new int[17];   // maxCode[length] = max code of that length
    private readonly int[] valOffset = new int[17];  // offset into values array
    private readonly byte[] values;

    public HuffmanTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> huffValues)
    {
        int totalValues = 0;
        for (int i = 0;i < 16;i++)
        {
            totalValues += bits[i];
        }

        values = huffValues[..totalValues].ToArray();

        // Generate code table following JPEG spec (Figure C.1)
        int code = 0;
        int valueIndex = 0;
        Span<int> minCode = stackalloc int[17];

        for (int len = 1;len <= 16;len++)
        {
            minCode[len] = code;
            valOffset[len] = valueIndex - code;

            int count = bits[len - 1];
            for (int i = 0;i < count;i++)
            {
                // Build fast lookup for short codes
                if (len <= MaxLookupBits)
                {
                    int padBits = MaxLookupBits - len;
                    int baseEntry = (ushort)((len << 8) | huffValues[valueIndex]);
                    // Fill all entries that share this prefix
                    for (int pad = 0;pad < (1 << padBits);pad++)
                    {
                        int index = (code << padBits) | pad;
                        fastLookup[index] = (ushort)baseEntry;
                    }
                }
                code++;
                valueIndex++;
            }

            maxCode[len] = code > 0 ? code - 1 : -1;
            code <<= 1;
        }
    }

    /// <summary>
    /// Decodes one Huffman symbol from the bit reader. Returns the decoded symbol value.
    /// </summary>
    public byte Decode(JpegBitReader reader)
    {
        if (reader.EndOfData)
        {
            return 0;
        }

        // Try fast lookup first
        int peek = reader.PeekBits(MaxLookupBits);
        if (reader.EndOfData)
        {
            return 0;
        }

        ushort entry = fastLookup[peek];
        int length = entry >> 8;

        if (length > 0)
        {
            reader.SkipBits(length);
            return (byte)(entry & 0xFF);
        }

        // Slow path for long codes
        int code = reader.ReadBits(9);
        if (reader.EndOfData)
        {
            return 0;
        }

        for (int len = 9;len <= 16;len++)
        {
            if (code <= maxCode[len])
            {
                return values[code + valOffset[len]];
            }

            code = (code << 1) | reader.ReadBit();
            if (reader.EndOfData)
            {
                return 0;
            }
        }

        return 0; // Invalid code, treat as EOB
    }
}

/// <summary>
/// Bit-level reader for JPEG entropy-coded data. Handles byte stuffing (0xFF 0x00 → 0xFF) and restart markers.
/// </summary>
public sealed class JpegBitReader
{
    private readonly Stream stream;
    private int bitBuffer;
    private int bitsRemaining;

    private bool endOfData;

    public JpegBitReader(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Whether the end of entropy-coded data has been reached.
    /// </summary>
    public bool EndOfData => endOfData;

    /// <summary>
    /// Reads a single bit (MSB first).
    /// </summary>
    public int ReadBit()
    {
        if (bitsRemaining == 0)
        {
            FillBuffer();
        }

        if (endOfData)
        {
            return 0;
        }

        bitsRemaining--;
        return (bitBuffer >> bitsRemaining) & 1;
    }

    /// <summary>
    /// Reads n bits (MSB first) and returns as an integer.
    /// </summary>
    public int ReadBits(int count)
    {
        if (endOfData)
        {
            return 0;
        }

        while (bitsRemaining < count)
        {
            FillBuffer();
            if (endOfData)
            {
                return 0;
            }
        }

        bitsRemaining -= count;
        return (bitBuffer >> bitsRemaining) & ((1 << count) - 1);
    }

    /// <summary>
    /// Peeks at the next n bits without consuming them.
    /// </summary>
    public int PeekBits(int count)
    {
        if (endOfData)
        {
            return 0;
        }

        while (bitsRemaining < count)
        {
            FillBuffer();
            if (endOfData)
            {
                return 0;
            }
        }

        return (bitBuffer >> (bitsRemaining - count)) & ((1 << count) - 1);
    }

    /// <summary>
    /// Skips n bits.
    /// </summary>
    public void SkipBits(int count)
    {
        if (endOfData)
        {
            return;
        }

        while (bitsRemaining < count)
        {
            FillBuffer();
            if (endOfData)
            {
                return;
            }
        }
        bitsRemaining -= count;
    }

    /// <summary>
    /// Resets bit buffer state (for restart markers).
    /// </summary>
    public void Reset()
    {
        bitBuffer = 0;
        bitsRemaining = 0;
        endOfData = false;
    }

    /// <summary>
    /// Extends a raw value to signed based on the category length. JPEG uses a convention where the first half of
    /// values in a category are negative.
    /// </summary>
    public static int Extend(int value, int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        int threshold = 1 << (bits - 1);
        return value < threshold ? value - (1 << bits) + 1 : value;
    }

    /// <summary>
    /// The marker byte that terminated the entropy-coded segment, or -1 if none.
    /// Used by progressive JPEG to resume marker parsing between scans.
    /// </summary>
    public int LastMarker { get; private set; } = -1;

    private void FillBuffer()
    {
        int b = stream.ReadByte();
        if (b < 0)
        {
            endOfData = true;
            return;
        }

        if (b == 0xFF)
        {
            int marker = stream.ReadByte();
            if (marker < 0)
            {
                endOfData = true;
                return;
            }
            if (marker != 0)
            {
                if (marker >= 0xD0 && marker <= 0xD7)
                {
                    // Restart marker: reset state
                    bitBuffer = 0;
                    bitsRemaining = 0;
                    return;
                }
                // EOI or other marker: end of entropy data
                LastMarker = marker;
                endOfData = true;
                // Seek back so the main parser can re-read this marker
                if (stream.CanSeek)
                {
                    stream.Seek(-2, SeekOrigin.Current);
                }
                return;
            }
            // 0xFF 0x00 = byte-stuffed 0xFF
        }

        bitBuffer = (bitBuffer << 8) | b;
        bitsRemaining += 8;
    }
}

/// <summary>
/// Bit-level writer for JPEG entropy-coded data. Handles byte stuffing (0xFF → 0xFF 0x00).
/// </summary>
public sealed class JpegBitWriter
{
    private readonly Stream stream;
    private int bitBuffer;
    private int bitsInBuffer;

    public JpegBitWriter(Stream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Writes n bits (MSB first) to the stream.
    /// </summary>
    public void WriteBits(int value, int count)
    {
        bitBuffer = (bitBuffer << count) | (value & ((1 << count) - 1));
        bitsInBuffer += count;

        while (bitsInBuffer >= 8)
        {
            bitsInBuffer -= 8;
            byte b = (byte)((bitBuffer >> bitsInBuffer) & 0xFF);
            stream.WriteByte(b);
            if (b == 0xFF)
            {
                stream.WriteByte(0); // Byte stuffing
            }
        }
    }

    /// <summary>
    /// Flushes remaining bits, padding with 1s per JPEG spec.
    /// </summary>
    public void Flush()
    {
        if (bitsInBuffer > 0)
        {
            int padding = 8 - bitsInBuffer;
            WriteBits((1 << padding) - 1, padding);
        }
    }

    /// <summary>
    /// Encodes a value into the JPEG category (number of extra bits) and the extra bits. Returns (category, bits).
    /// </summary>
    public static (int category, int bits) EncodeValue(int value)
    {
        int absValue = value < 0 ? -value : value;
        int category = 0;
        int temp = absValue;
        while (temp != 0)
        {
            category++;
            temp >>= 1;
        }
        int bits = value >= 0 ? value : value + (1 << category) - 1;
        return (category, bits);
    }
}
