// CCITT Group 3 (T.4) and Group 4 (T.6) fax compression decoder.
// Implements Modified Huffman (1D), Group 3 (1D with EOL), and Group 4 (2D) decoding.
// Standard code tables from ITU-T Recommendation T.4/T.6.
// Output: one byte per pixel (0 = white, 1 = black).

using System.Runtime.CompilerServices;

namespace SharpImage.Compression;

public static class CcittFaxDecoder
{
    // Terminating codes for white runs (0-63 pixels)
    // Format: (codeword, bit length, run length)
    private static readonly (int Code, int Bits, int RunLength)[] WhiteTerminating =
    [
        (0b00110101, 8,  0),
        (0b000111,   6,  1),
        (0b0111,     4,  2),
        (0b1000,     4,  3),
        (0b1011,     4,  4),
        (0b1100,     4,  5),
        (0b1110,     4,  6),
        (0b1111,     4,  7),
        (0b10011,    5,  8),
        (0b10100,    5,  9),
        (0b00111,    5, 10),
        (0b01000,    5, 11),
        (0b001000,   6, 12),
        (0b000011,   6, 13),
        (0b110100,   6, 14),
        (0b110101,   6, 15),
        (0b101010,   6, 16),
        (0b101011,   6, 17),
        (0b0100111,  7, 18),
        (0b0001100,  7, 19),
        (0b0001000,  7, 20),
        (0b0010111,  7, 21),
        (0b0000011,  7, 22),
        (0b0000100,  7, 23),
        (0b0101000,  7, 24),
        (0b0101011,  7, 25),
        (0b0010011,  7, 26),
        (0b0100100,  7, 27),
        (0b0011000,  7, 28),
        (0b00000010, 8, 29),
        (0b00000011, 8, 30),
        (0b00011010, 8, 31),
        (0b00011011, 8, 32),
        (0b00010010, 8, 33),
        (0b00010011, 8, 34),
        (0b00010100, 8, 35),
        (0b00010101, 8, 36),
        (0b00010110, 8, 37),
        (0b00010111, 8, 38),
        (0b00101000, 8, 39),
        (0b00101001, 8, 40),
        (0b00101010, 8, 41),
        (0b00101011, 8, 42),
        (0b00101100, 8, 43),
        (0b00101101, 8, 44),
        (0b00000100, 8, 45),
        (0b00000101, 8, 46),
        (0b00001010, 8, 47),
        (0b00001011, 8, 48),
        (0b01010010, 8, 49),
        (0b01010011, 8, 50),
        (0b01010100, 8, 51),
        (0b01010101, 8, 52),
        (0b00100100, 8, 53),
        (0b00100101, 8, 54),
        (0b01011000, 8, 55),
        (0b01011001, 8, 56),
        (0b01011010, 8, 57),
        (0b01011011, 8, 58),
        (0b01001010, 8, 59),
        (0b01001011, 8, 60),
        (0b00110010, 8, 61),
        (0b00110011, 8, 62),
        (0b00110100, 8, 63),
    ];

    // Terminating codes for black runs (0-63 pixels)
    private static readonly (int Code, int Bits, int RunLength)[] BlackTerminating =
    [
        (0b0000110111,  10,  0),
        (0b010,          3,  1),
        (0b11,           2,  2),
        (0b10,           2,  3),
        (0b011,          3,  4),
        (0b0011,         4,  5),
        (0b0010,         4,  6),
        (0b00011,        5,  7),
        (0b000101,       6,  8),
        (0b000100,       6,  9),
        (0b0000100,      7, 10),
        (0b0000101,      7, 11),
        (0b0000111,      7, 12),
        (0b00000100,     8, 13),
        (0b00000111,     8, 14),
        (0b000011000,    9, 15),
        (0b0000010111,  10, 16),
        (0b0000011000,  10, 17),
        (0b0000001000,  10, 18),
        (0b00001100111, 11, 19),
        (0b00001101000, 11, 20),
        (0b00001101100, 11, 21),
        (0b00000110111, 11, 22),
        (0b00000101000, 11, 23),
        (0b00000010111, 11, 24),
        (0b00000011000, 11, 25),
        (0b000011001010, 12, 26),
        (0b000011001011, 12, 27),
        (0b000011001100, 12, 28),
        (0b000011001101, 12, 29),
        (0b000001101000, 12, 30),
        (0b000001101001, 12, 31),
        (0b000001101010, 12, 32),
        (0b000001101011, 12, 33),
        (0b000011010010, 12, 34),
        (0b000011010011, 12, 35),
        (0b000011010100, 12, 36),
        (0b000011010101, 12, 37),
        (0b000011010110, 12, 38),
        (0b000011010111, 12, 39),
        (0b000001101100, 12, 40),
        (0b000001101101, 12, 41),
        (0b000011011010, 12, 42),
        (0b000011011011, 12, 43),
        (0b000001010100, 12, 44),
        (0b000001010101, 12, 45),
        (0b000001010110, 12, 46),
        (0b000001010111, 12, 47),
        (0b000001100100, 12, 48),
        (0b000001100101, 12, 49),
        (0b000001010010, 12, 50),
        (0b000001010011, 12, 51),
        (0b000000100100, 12, 52),
        (0b000000110111, 12, 53),
        (0b000000111000, 12, 54),
        (0b000000100111, 12, 55),
        (0b000000101000, 12, 56),
        (0b000001011000, 12, 57),
        (0b000001011001, 12, 58),
        (0b000000101011, 12, 59),
        (0b000000101100, 12, 60),
        (0b000001011010, 12, 61),
        (0b000001100110, 12, 62),
        (0b000001100111, 12, 63),
    ];

    // Makeup codes for white runs (64-2560 pixels, steps of 64)
    private static readonly (int Code, int Bits, int RunLength)[] WhiteMakeup =
    [
        (0b11011,     5,   64),
        (0b10010,     5,  128),
        (0b010111,    6,  192),
        (0b0110111,   7,  256),
        (0b00110110,  8,  320),
        (0b00110111,  8,  384),
        (0b01100100,  8,  448),
        (0b01100101,  8,  512),
        (0b01101000,  8,  576),
        (0b01100111,  8,  640),
        (0b011001100, 9,  704),
        (0b011001101, 9,  768),
        (0b011010010, 9,  832),
        (0b011010011, 9,  896),
        (0b011010100, 9,  960),
        (0b011010101, 9, 1024),
        (0b011010110, 9, 1088),
        (0b011010111, 9, 1152),
        (0b011011000, 9, 1216),
        (0b011011001, 9, 1280),
        (0b011011010, 9, 1344),
        (0b011011011, 9, 1408),
        (0b010011000, 9, 1472),
        (0b010011001, 9, 1536),
        (0b010011010, 9, 1600),
        (0b011000,    6, 1664),
        (0b010011011, 9, 1728),
    ];

    // Makeup codes for black runs (64-2560 pixels, steps of 64)
    private static readonly (int Code, int Bits, int RunLength)[] BlackMakeup =
    [
        (0b0000001111,  10,   64),
        (0b000011001000, 12,  128),
        (0b000011001001, 12,  192),
        (0b000001011011, 12,  256),
        (0b000000110011, 12,  320),
        (0b000000110100, 12,  384),
        (0b000000110101, 12,  448),
        (0b0000001101100, 13,  512),
        (0b0000001101101, 13,  576),
        (0b0000001001010, 13,  640),
        (0b0000001001011, 13,  704),
        (0b0000001001100, 13,  768),
        (0b0000001001101, 13,  832),
        (0b0000001110010, 13,  896),
        (0b0000001110011, 13,  960),
        (0b0000001110100, 13, 1024),
        (0b0000001110101, 13, 1088),
        (0b0000001110110, 13, 1152),
        (0b0000001110111, 13, 1216),
        (0b0000001010010, 13, 1280),
        (0b0000001010011, 13, 1344),
        (0b0000001010100, 13, 1408),
        (0b0000001010101, 13, 1472),
        (0b0000001011010, 13, 1536),
        (0b0000001011011, 13, 1600),
        (0b0000001100100, 13, 1664),
        (0b0000001100101, 13, 1728),
    ];

    // Extended makeup codes shared by white and black (1792-2560)
    private static readonly (int Code, int Bits, int RunLength)[] ExtendedMakeup =
    [
        (0b00000001000, 11, 1792),
        (0b00000001100, 11, 1856),
        (0b00000001101, 11, 1920),
        (0b000000010010, 12, 1984),
        (0b000000010011, 12, 2048),
        (0b000000010100, 12, 2112),
        (0b000000010101, 12, 2176),
        (0b000000010110, 12, 2240),
        (0b000000010111, 12, 2304),
        (0b000000011100, 12, 2368),
        (0b000000011101, 12, 2432),
        (0b000000011110, 12, 2496),
        (0b000000011111, 12, 2560),
    ];

    // Group 4 mode codes
    private const int PassModeCode = 0b0001;
    private const int PassModeBits = 4;
    private const int HorizontalModeCode = 0b001;
    private const int HorizontalModeBits = 3;

    // Vertical mode codes: V(0)=1, V(1)=011, V(-1)=010, V(2)=000011, V(-2)=000010, V(3)=0000011, V(-3)=0000010
    private static readonly (int Code, int Bits, int Offset)[] VerticalModes =
    [
        (0b1,       1,  0),
        (0b011,     3,  1),
        (0b010,     3, -1),
        (0b000011,  6,  2),
        (0b000010,  6, -2),
        (0b0000011, 7,  3),
        (0b0000010, 7, -3),
    ];

    // Lookup tables built lazily for O(1) code matching
    private static HuffmanEntry[]? whiteTable;
    private static HuffmanEntry[]? blackTable;
    private static readonly object tableLock = new();

    private struct HuffmanEntry
    {
        public int RunLength;
        public bool IsTerminating;
        public bool IsValid;
    }

    // Maximum code length in bits
    private const int MaxCodeBits = 13;
    private const int TableSize = 1 << MaxCodeBits; // 8192 entries

    private static void EnsureTables()
    {
        if (whiteTable != null) return;
        lock (tableLock)
        {
            if (whiteTable != null) return;

            var wt = new HuffmanEntry[TableSize];
            var bt = new HuffmanEntry[TableSize];

            // Build white table
            foreach (var (code, bits, run) in WhiteTerminating)
                FillTable(wt, code, bits, run, isTerminating: true);
            foreach (var (code, bits, run) in WhiteMakeup)
                FillTable(wt, code, bits, run, isTerminating: false);
            foreach (var (code, bits, run) in ExtendedMakeup)
                FillTable(wt, code, bits, run, isTerminating: false);

            // Build black table
            foreach (var (code, bits, run) in BlackTerminating)
                FillTable(bt, code, bits, run, isTerminating: true);
            foreach (var (code, bits, run) in BlackMakeup)
                FillTable(bt, code, bits, run, isTerminating: false);
            foreach (var (code, bits, run) in ExtendedMakeup)
                FillTable(bt, code, bits, run, isTerminating: false);

            blackTable = bt;
            whiteTable = wt;
        }
    }

    private static void FillTable(HuffmanEntry[] table, int code, int bits, int runLength, bool isTerminating)
    {
        // For codes shorter than MaxCodeBits, fill all entries where the remaining bits can be anything
        int shift = MaxCodeBits - bits;
        int baseIndex = code << shift;
        int count = 1 << shift;
        for (int i = 0; i < count; i++)
        {
            table[baseIndex | i] = new HuffmanEntry
            {
                RunLength = runLength,
                IsTerminating = isTerminating,
                IsValid = true
            };
        }
    }

    /// <summary>
    /// Decode CCITT Modified Huffman (compression type 2) — 1D without EOL markers.
    /// Each strip is decoded independently, line by line.
    /// </summary>
    public static byte[] DecodeModifiedHuffman(ReadOnlySpan<byte> data, int width, int rows)
    {
        EnsureTables();
        var output = new byte[width * rows];
        var reader = new BitReader(data);

        for (int y = 0; y < rows; y++)
        {
            int offset = y * width;
            DecodeOneLine1D(ref reader, output, offset, width);
            reader.AlignToByte();
        }

        return output;
    }

    /// <summary>
    /// Decode CCITT Group 3 (T.4) — 1D with EOL markers.
    /// </summary>
    public static byte[] DecodeGroup3(ReadOnlySpan<byte> data, int width, int rows, int t4Options = 0)
    {
        EnsureTables();
        var output = new byte[width * rows];
        var reader = new BitReader(data);

        // Skip initial fill bits and EOL
        SkipEol(ref reader);

        for (int y = 0; y < rows; y++)
        {
            int offset = y * width;
            DecodeOneLine1D(ref reader, output, offset, width);

            // Skip EOL marker for next line (or RTC)
            if (y < rows - 1)
                SkipEol(ref reader);
        }

        return output;
    }

    /// <summary>
    /// Decode CCITT Group 4 (T.6) — 2D encoding using reference line.
    /// </summary>
    public static byte[] DecodeGroup4(ReadOnlySpan<byte> data, int width, int rows)
    {
        EnsureTables();
        var output = new byte[width * rows];
        var reader = new BitReader(data);

        // Reference line starts as all white
        var referenceLine = new byte[width];

        for (int y = 0; y < rows; y++)
        {
            int offset = y * width;
            DecodeOneLine2D(ref reader, output, offset, width, referenceLine);

            // Current line becomes the reference for next line
            if (y < rows - 1)
                Array.Copy(output, offset, referenceLine, 0, width);
        }

        return output;
    }

    private static void DecodeOneLine1D(ref BitReader reader, byte[] output, int offset, int width)
    {
        int col = 0;
        bool isWhite = true;

        while (col < width)
        {
            int run = DecodeRun(ref reader, isWhite);
            if (run < 0) break; // error or end of data

            run = Math.Min(run, width - col);
            if (!isWhite)
            {
                // Fill black pixels
                for (int i = 0; i < run; i++)
                    output[offset + col + i] = 1;
            }
            // White pixels are already 0 (default)

            col += run;
            isWhite = !isWhite;
        }
    }

    private static void DecodeOneLine2D(ref BitReader reader, byte[] output, int offset,
        int width, byte[] referenceLine)
    {
        int a0 = 0; // current position on coding line
        bool isWhite = true;

        while (a0 < width)
        {
            // Try to match a mode code
            int mode = DecodeMode(ref reader);

            switch (mode)
            {
                case 0: // Pass mode
                {
                    // b1 = first changing element on ref opposite to coding color
                    // b2 = next changing element after b1 (same color as coding)
                    int b1 = FindChangingElement(referenceLine, a0, width, isWhite);
                    int b2 = FindChangingElement(referenceLine, b1, width, !isWhite);
                    // a0 moves to below b2 without changing color
                    if (!isWhite)
                    {
                        for (int i = a0; i < b2 && i < width; i++)
                            output[offset + i] = 1;
                    }
                    a0 = b2;
                    break;
                }

                case 1: // Horizontal mode
                {
                    // Read two runs (same as 1D coding)
                    int run1 = DecodeRun(ref reader, isWhite);
                    int run2 = DecodeRun(ref reader, !isWhite);
                    if (run1 < 0 || run2 < 0) return;

                    run1 = Math.Min(run1, width - a0);
                    if (!isWhite)
                    {
                        for (int i = 0; i < run1; i++)
                            output[offset + a0 + i] = 1;
                    }
                    a0 += run1;

                    run2 = Math.Min(run2, width - a0);
                    if (isWhite) // second run is opposite color
                    {
                        for (int i = 0; i < run2; i++)
                            output[offset + a0 + i] = 1;
                    }
                    a0 += run2;
                    break;
                }

                default: // Vertical mode: offset is mode - 100
                {
                    int verticalOffset = mode - 100;
                    // b1 = first changing element opposite to coding color
                    int b1 = FindChangingElement(referenceLine, a0, width, isWhite);
                    int a1 = b1 + verticalOffset;
                    a1 = Math.Max(a0, Math.Min(a1, width));

                    if (!isWhite)
                    {
                        for (int i = a0; i < a1 && i < width; i++)
                            output[offset + i] = 1;
                    }
                    a0 = a1;
                    isWhite = !isWhite;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Find the next changing element on the reference line at or after startPos
    /// that has the specified color. seekBlack=true means look for black (1) pixels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindChangingElement(byte[] line, int startPos, int width, bool seekBlack)
    {
        byte target = seekBlack ? (byte)1 : (byte)0;
        for (int pos = startPos; pos < width; pos++)
        {
            if (line[pos] == target)
                return pos;
        }
        return width;
    }

    /// <summary>
    /// Decode a complete run (possibly with makeup codes followed by terminating code).
    /// </summary>
    private static int DecodeRun(ref BitReader reader, bool isWhite)
    {
        var table = isWhite ? whiteTable! : blackTable!;
        int totalRun = 0;

        while (true)
        {
            if (!reader.HasBits(1)) return totalRun > 0 ? totalRun : -1;

            int bits = reader.PeekBits(MaxCodeBits);
            if (bits < 0) return totalRun > 0 ? totalRun : -1;

            var entry = table[bits];
            if (!entry.IsValid)
            {
                // Try to skip a bad bit and continue
                reader.SkipBits(1);
                continue;
            }

            // Determine actual code length to consume
            int codeLen = GetCodeLength(isWhite, entry.RunLength, entry.IsTerminating);
            reader.SkipBits(codeLen);

            totalRun += entry.RunLength;

            if (entry.IsTerminating)
                return totalRun;
        }
    }

    private static int GetCodeLength(bool isWhite, int runLength, bool isTerminating)
    {
        if (isTerminating)
        {
            var table = isWhite ? WhiteTerminating : BlackTerminating;
            if (runLength < table.Length)
                return table[runLength].Bits;
        }
        else
        {
            // Makeup code — search in the appropriate table
            var makeup = isWhite ? WhiteMakeup : BlackMakeup;
            foreach (var (_, bits, run) in makeup)
            {
                if (run == runLength) return bits;
            }
            foreach (var (_, bits, run) in ExtendedMakeup)
            {
                if (run == runLength) return bits;
            }
        }
        return 1; // fallback
    }

    /// <summary>
    /// Decode a Group 4 mode code. Returns:
    /// 0 = Pass, 1 = Horizontal, 100+offset = Vertical(offset)
    /// </summary>
    private static int DecodeMode(ref BitReader reader)
    {
        if (!reader.HasBits(1)) return 100; // default to V(0) on end of data

        // V(0) = 1 (1 bit)
        if (reader.PeekBit() == 1)
        {
            reader.SkipBits(1);
            return 100; // vertical offset 0
        }

        // Need more bits
        if (!reader.HasBits(3)) return 100;
        int bits3 = reader.PeekBits(3);

        // V(1) = 011 (3 bits)
        if (bits3 == 0b011)
        {
            reader.SkipBits(3);
            return 101; // vertical offset +1
        }

        // V(-1) = 010 (3 bits)
        if (bits3 == 0b010)
        {
            reader.SkipBits(3);
            return 99; // vertical offset -1
        }

        // Horizontal = 001 (3 bits)
        if (bits3 == 0b001)
        {
            reader.SkipBits(3);
            return 1;
        }

        // Need 4 bits for Pass = 0001
        if (!reader.HasBits(4)) return 100;
        int bits4 = reader.PeekBits(4);
        if (bits4 == 0b0001)
        {
            reader.SkipBits(4);
            return 0;
        }

        // Need 6 bits for V(2)/V(-2)
        if (!reader.HasBits(6)) return 100;
        int bits6 = reader.PeekBits(6);
        if (bits6 == 0b000011)
        {
            reader.SkipBits(6);
            return 102; // vertical offset +2
        }
        if (bits6 == 0b000010)
        {
            reader.SkipBits(6);
            return 98; // vertical offset -2
        }

        // Need 7 bits for V(3)/V(-3)
        if (!reader.HasBits(7)) return 100;
        int bits7 = reader.PeekBits(7);
        if (bits7 == 0b0000011)
        {
            reader.SkipBits(7);
            return 103; // vertical offset +3
        }
        if (bits7 == 0b0000010)
        {
            reader.SkipBits(7);
            return 97; // vertical offset -3
        }

        // Unknown code, skip a bit
        reader.SkipBits(1);
        return 100;
    }

    private static void SkipEol(ref BitReader reader)
    {
        // EOL is 000000000001 (11 zeros + 1 one)
        // Skip any fill bits (zeros) until we find the EOL pattern
        // Some encoders pad to byte boundary with fill bits before EOL
        int zerosFound = 0;
        int maxBitsToSearch = 24; // reasonable limit
        int searched = 0;

        while (reader.HasBits(1) && searched < maxBitsToSearch)
        {
            if (reader.PeekBit() == 0)
            {
                reader.SkipBits(1);
                zerosFound++;
            }
            else
            {
                // Found a 1
                reader.SkipBits(1);
                if (zerosFound >= 11)
                    return; // valid EOL found
                zerosFound = 0;
            }
            searched++;
        }
    }

    /// <summary>
    /// Bit reader that reads MSB-first from a byte stream.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int bytePos;
        private int bitPos; // 0-7, 0 = MSB

        public BitReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            bytePos = 0;
            bitPos = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasBits(int count)
        {
            long totalBitsLeft = ((long)(data.Length - bytePos) * 8) - bitPos;
            return totalBitsLeft >= count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int PeekBit()
        {
            if (bytePos >= data.Length) return 0;
            return (data[bytePos] >> (7 - bitPos)) & 1;
        }

        /// <summary>
        /// Peek up to MaxCodeBits bits without advancing position.
        /// Bits are read MSB-first and left-aligned in the result.
        /// </summary>
        public int PeekBits(int count)
        {
            if (count <= 0 || count > MaxCodeBits) return -1;

            int result = 0;
            int bPos = bytePos;
            int bBit = bitPos;
            int bitsRead = 0;

            for (int i = 0; i < count; i++)
            {
                if (bPos >= data.Length) break;
                int bit = (data[bPos] >> (7 - bBit)) & 1;
                result = (result << 1) | bit;
                bBit++;
                if (bBit >= 8)
                {
                    bBit = 0;
                    bPos++;
                }
                bitsRead++;
            }

            // Left-align: if fewer bits were read, shift so codes still match lookup table
            result <<= (count - bitsRead);

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SkipBits(int count)
        {
            bitPos += count;
            bytePos += bitPos >> 3;
            bitPos &= 7;
        }

        public void AlignToByte()
        {
            if (bitPos > 0)
            {
                bitPos = 0;
                bytePos++;
            }
        }
    }
}
