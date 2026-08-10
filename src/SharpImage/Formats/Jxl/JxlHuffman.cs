// JPEG XL prefix (Huffman) code decoder, ported from libjxl (dec_huffman.cc, huffman_table.cc).
// Validated against a pure-Python prototype that decodes libjxl-produced lossless files
// bit-exactly. Part of the from-scratch pure-C# JPEG XL decoder.
using System;

namespace SharpImage.Formats.Jxl;

/// <summary>A prefix-code table entry: number of bits consumed and the decoded value.</summary>
internal struct HuffCode
{
    public int Bits;
    public int Value;
}

/// <summary>Decodes a JPEG XL prefix (Huffman) code and reads symbols from it.</summary>
internal sealed class JxlHuffman
{
    public const int PrefixMaxBits = 15;
    public const int TableBits = 8;
    private const int CodeLengthCodes = 18;
    private static readonly int[] CodeLengthCodeOrder =
        [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private const int DefaultCodeLength = 8;
    private const int RepeatCode = 16;

    private readonly HuffCode[] table;

    /// <summary>A trivial single-symbol code (alphabet size &lt;= 1): always returns 0, consuming no bits.</summary>
    public bool Trivial { get; }

    private JxlHuffman()
    {
        Trivial = true;
        table = [];
    }

    public static JxlHuffman TrivialCode() => new();

    private static int FloorLog2(uint x) => 31 - System.Numerics.BitOperations.LeadingZeroCount(x);

    private static int GetNextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0)
        {
            step >>= 1;
        }

        return (key & (step - 1)) + step;
    }

    private static void ReplicateValue(HuffCode[] t, int baseIdx, int step, int end, HuffCode code)
    {
        int e = end;
        do
        {
            e -= step;
            t[baseIdx + e] = code;
        }
        while (e > 0);
    }

    private static int NextTableBitSize(int[] count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < PrefixMaxBits)
        {
            if (left <= count[len])
            {
                break;
            }

            left -= count[len];
            len++;
            left <<= 1;
        }

        return len - rootBits;
    }

    private static int BuildTable(HuffCode[] t, int rootBits, int[] codeLengths, int[] count)
    {
        int size = codeLengths.Length;
        if (size > (1 << PrefixMaxBits))
        {
            return 0;
        }

        int[] offset = new int[PrefixMaxBits + 1];
        int[] sorted = new int[size];
        int sum = 0;
        int maxLength = 1;
        for (int len = 1; len <= PrefixMaxBits; len++)
        {
            offset[len] = sum;
            if (count[len] != 0)
            {
                sum += count[len];
                maxLength = len;
            }
        }

        for (int sym = 0; sym < size; sym++)
        {
            if (codeLengths[sym] != 0)
            {
                sorted[offset[codeLengths[sym]]++] = sym;
            }
        }

        int tableBase = 0;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;

        if (offset[PrefixMaxBits] == 1)
        {
            var code0 = new HuffCode { Bits = 0, Value = sorted[0] };
            for (int key0 = 0; key0 < totalSize; key0++)
            {
                t[key0] = code0;
            }

            return totalSize;
        }

        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }

        int key = 0;
        int symbol = 0;
        int cbits = 1;
        int step = 2;
        while (true)
        {
            for (; count[cbits] != 0; count[cbits]--)
            {
                var code = new HuffCode { Bits = cbits, Value = sorted[symbol++] };
                ReplicateValue(t, tableBase + key, step, tableSize, code);
                key = GetNextKey(key, cbits);
            }

            step <<= 1;
            cbits++;
            if (cbits > tableBits)
            {
                break;
            }
        }

        while (totalSize != tableSize)
        {
            Array.Copy(t, 0, t, tableSize, tableSize);
            tableSize <<= 1;
        }

        int mask = totalSize - 1;
        int low = -1;
        int len2 = rootBits + 1;
        step = 2;
        while (len2 <= maxLength)
        {
            for (; count[len2] != 0; count[len2]--)
            {
                if ((key & mask) != low)
                {
                    tableBase += tableSize;
                    tableBits = NextTableBitSize(count, len2, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    low = key & mask;
                    t[low].Bits = tableBits + rootBits;
                    t[low].Value = tableBase - low;
                }

                var code = new HuffCode { Bits = len2 - rootBits, Value = sorted[symbol++] };
                ReplicateValue(t, tableBase + (key >> rootBits), step, tableSize, code);
                key = GetNextKey(key, len2);
            }

            len2++;
            step <<= 1;
        }

        return totalSize;
    }

    private static bool ReadHuffmanCodeLengths(int[] clcLengths, int numSymbols, JxlBitReader br, int[] outLengths)
    {
        int symbol = 0;
        int prevCodeLen = DefaultCodeLength;
        int repeat = 0;
        int repeatCodeLen = 0;
        int space = 32768;
        var t = new HuffCode[32];
        int[] counts = new int[16];
        foreach (int cl in clcLengths)
        {
            counts[cl]++;
        }

        if (BuildTable(t, 5, clcLengths, counts) == 0)
        {
            return false;
        }

        while (symbol < numSymbols && space > 0)
        {
            HuffCode p = t[br.PeekBits(5)];
            br.Consume(p.Bits);
            int codeLen = p.Value;
            if (codeLen < RepeatCode)
            {
                repeat = 0;
                outLengths[symbol++] = codeLen;
                if (codeLen != 0)
                {
                    prevCodeLen = codeLen;
                    space -= 32768 >> codeLen;
                }
            }
            else
            {
                int extraBits = codeLen - 14;
                int newLen = codeLen == RepeatCode ? prevCodeLen : 0;
                if (repeatCodeLen != newLen)
                {
                    repeat = 0;
                    repeatCodeLen = newLen;
                }

                int oldRepeat = repeat;
                if (repeat > 0)
                {
                    repeat = (repeat - 2) << extraBits;
                }

                repeat += (int)br.ReadBits(extraBits) + 3;
                int repeatDelta = repeat - oldRepeat;
                if (symbol + repeatDelta > numSymbols)
                {
                    return false;
                }

                for (int k = 0; k < repeatDelta; k++)
                {
                    outLengths[symbol + k] = repeatCodeLen;
                }

                symbol += repeatDelta;
                if (repeatCodeLen != 0)
                {
                    space -= repeatDelta << (15 - repeatCodeLen);
                }
            }
        }

        return space == 0;
    }

    private static readonly HuffCode[] SimpleClcHuff =
    [
        new() { Bits = 2, Value = 0 }, new() { Bits = 2, Value = 4 }, new() { Bits = 2, Value = 3 }, new() { Bits = 3, Value = 2 },
        new() { Bits = 2, Value = 0 }, new() { Bits = 2, Value = 4 }, new() { Bits = 2, Value = 3 }, new() { Bits = 4, Value = 1 },
        new() { Bits = 2, Value = 0 }, new() { Bits = 2, Value = 4 }, new() { Bits = 2, Value = 3 }, new() { Bits = 3, Value = 2 },
        new() { Bits = 2, Value = 0 }, new() { Bits = 2, Value = 4 }, new() { Bits = 2, Value = 3 }, new() { Bits = 4, Value = 5 },
    ];

    private static bool ReadSimpleCode(int alphabetSize, JxlBitReader br, HuffCode[] t)
    {
        int maxBits = alphabetSize > 1 ? FloorLog2((uint)(alphabetSize - 1)) + 1 : 0;
        int num = (int)br.ReadBits(2) + 1;
        int[] syms = new int[4];
        for (int i = 0; i < num; i++)
        {
            int sym = (int)br.ReadBits(maxBits);
            if (sym >= alphabetSize)
            {
                return false;
            }

            syms[i] = sym;
        }

        for (int i = 0; i < num - 1; i++)
        {
            for (int j = i + 1; j < num; j++)
            {
                if (syms[i] == syms[j])
                {
                    return false;
                }
            }
        }

        if (num == 4)
        {
            num += (int)br.ReadBits(1);
        }

        void Swap(int i, int j) => (syms[i], syms[j]) = (syms[j], syms[i]);

        int ts;
        switch (num)
        {
            case 1:
                t[0] = new HuffCode { Bits = 0, Value = syms[0] };
                ts = 1;
                break;
            case 2:
                if (syms[0] > syms[1])
                {
                    Swap(0, 1);
                }

                t[0] = new HuffCode { Bits = 1, Value = syms[0] };
                t[1] = new HuffCode { Bits = 1, Value = syms[1] };
                ts = 2;
                break;
            case 3:
                if (syms[1] > syms[2])
                {
                    Swap(1, 2);
                }

                t[0] = new HuffCode { Bits = 1, Value = syms[0] };
                t[2] = new HuffCode { Bits = 1, Value = syms[0] };
                t[1] = new HuffCode { Bits = 2, Value = syms[1] };
                t[3] = new HuffCode { Bits = 2, Value = syms[2] };
                ts = 4;
                break;
            case 4:
                for (int i = 0; i < 3; i++)
                {
                    for (int j = i + 1; j < 4; j++)
                    {
                        if (syms[i] > syms[j])
                        {
                            Swap(i, j);
                        }
                    }
                }

                t[0] = new HuffCode { Bits = 2, Value = syms[0] };
                t[2] = new HuffCode { Bits = 2, Value = syms[1] };
                t[1] = new HuffCode { Bits = 2, Value = syms[2] };
                t[3] = new HuffCode { Bits = 2, Value = syms[3] };
                ts = 4;
                break;
            case 5:
                if (syms[2] > syms[3])
                {
                    Swap(2, 3);
                }

                t[0] = new HuffCode { Bits = 1, Value = syms[0] };
                t[1] = new HuffCode { Bits = 2, Value = syms[1] };
                t[2] = new HuffCode { Bits = 1, Value = syms[0] };
                t[3] = new HuffCode { Bits = 3, Value = syms[2] };
                t[4] = new HuffCode { Bits = 1, Value = syms[0] };
                t[5] = new HuffCode { Bits = 2, Value = syms[1] };
                t[6] = new HuffCode { Bits = 1, Value = syms[0] };
                t[7] = new HuffCode { Bits = 3, Value = syms[3] };
                ts = 8;
                break;
            default:
                return false;
        }

        int goal = 1 << TableBits;
        while (ts != goal)
        {
            Array.Copy(t, 0, t, ts, ts);
            ts <<= 1;
        }

        return true;
    }

    public JxlHuffman(int alphabetSize, JxlBitReader br)
    {
        uint simpleOrSkip = br.ReadBits(2);
        if (simpleOrSkip == 1)
        {
            table = new HuffCode[1 << TableBits];
            if (!ReadSimpleCode(alphabetSize, br, table))
            {
                throw new InvalidOperationException("Invalid simple prefix code.");
            }

            return;
        }

        int[] codeLengths = new int[alphabetSize];
        int[] clc = new int[CodeLengthCodes];
        int space = 32;
        int numCodes = 0;
        for (int i = (int)simpleOrSkip; i < CodeLengthCodes && space > 0; i++)
        {
            int idx = CodeLengthCodeOrder[i];
            HuffCode p = SimpleClcHuff[br.PeekBits(4)];
            br.Consume(p.Bits);
            int v = p.Value;
            clc[idx] = v;
            if (v != 0)
            {
                space -= 32 >> v;
                numCodes++;
            }
        }

        if (!(numCodes == 1 || space == 0) || !ReadHuffmanCodeLengths(clc, alphabetSize, br, codeLengths))
        {
            throw new InvalidOperationException("Invalid prefix code lengths.");
        }

        int[] counts = new int[16];
        for (int i = 0; i < alphabetSize; i++)
        {
            counts[codeLengths[i]]++;
        }

        var t = new HuffCode[alphabetSize + 376];
        int tableSize = BuildTable(t, TableBits, codeLengths, counts);
        if (tableSize <= 0)
        {
            throw new InvalidOperationException("Failed to build prefix table.");
        }

        table = new HuffCode[tableSize];
        Array.Copy(t, table, tableSize);
    }

    public int ReadSymbol(JxlBitReader br)
    {
        if (Trivial)
        {
            return 0;
        }

        int idx = (int)br.PeekBits(TableBits);
        HuffCode e = table[idx];
        int nBits = e.Bits;
        if (nBits > TableBits)
        {
            br.Consume(TableBits);
            nBits -= TableBits;
            idx = idx + e.Value + (int)br.PeekBits(nBits);
            e = table[idx];
        }

        br.Consume(e.Bits);
        return e.Value;
    }

    public static int DecodeVarLenUint16(JxlBitReader br)
    {
        if (br.ReadBits(1) != 0)
        {
            int nbits = (int)br.ReadBits(4);
            return nbits == 0 ? 1 : (int)br.ReadBits(nbits) + (1 << nbits);
        }

        return 0;
    }
}
