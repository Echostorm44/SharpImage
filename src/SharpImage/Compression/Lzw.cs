using System.Runtime.CompilerServices;

namespace SharpImage.Compression;

/// <summary>
/// LZW (Lempel-Ziv-Welch) compression and decompression for GIF format. Implements variable-width codes from
/// minCodeSize+1 bits up to 12 bits max.
/// </summary>
public static class Lzw
{
    private const int MaxCodeSize = 12;
    private const int MaxTableSize = 1 << MaxCodeSize; // 4096

    /// <summary>
    /// Decompresses LZW-encoded data from a GIF sub-block stream. Returns the decompressed byte array.
    /// </summary>
    public static byte[] Decompress(Stream stream, int minCodeSize)
    {
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;

        // String table: each entry is (prefix index, suffix byte)
        int[] prefix = new int[MaxTableSize];
        byte[] suffix = new byte[MaxTableSize];
        int[] lengths = new int[MaxTableSize]; // length of string at each code

        // Initialize table with single-byte entries
        for (int i = 0;i < clearCode;i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            lengths[i] = 1;
        }

        // Output buffer
        var output = new List<byte>(8192);
        byte[] decodeStack = new byte[MaxTableSize];

        // Bit reader state
        var bitReader = new GifBitReader(stream);

        int prevCode = -1;
        byte firstByte = 0;

        while (true)
        {
            int code = bitReader.ReadBits(codeSize);
            if (code < 0)
            {
                break;
            }

            if (code == endCode)
            {
                break;
            }

            if (code == clearCode)
            {
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
                prevCode = -1;
                continue;
            }

            int currentCode = code;
            int stackIndex = 0;

            // Validate code range
            if (code > nextCode || code >= MaxTableSize)
            {
                continue; // Skip invalid code
            }

            if (code >= nextCode)
            {
                // Special case: code not yet in table
                decodeStack[stackIndex++] = firstByte;
                currentCode = prevCode;
            }

            // Unwind the string table to get the output bytes
            while (currentCode >= clearCode + 2)
            {
                if (currentCode >= MaxTableSize || stackIndex >= MaxTableSize)
                {
                    break; // Safety: prevent corrupt data from crashing
                }

                decodeStack[stackIndex++] = suffix[currentCode];
                currentCode = prefix[currentCode];
            }
            if (currentCode < 0 || currentCode >= MaxTableSize || stackIndex >= MaxTableSize)
            {
                continue;
            }

            decodeStack[stackIndex++] = suffix[currentCode];
            firstByte = decodeStack[stackIndex - 1];

            // Output in reverse order
            for (int i = stackIndex - 1;i >= 0;i--)
            {
                output.Add(decodeStack[i]);
            }

            // Add new entry to table
            if (prevCode >= 0 && nextCode < MaxTableSize)
            {
                prefix[nextCode] = prevCode;
                suffix[nextCode] = firstByte;
                lengths[nextCode] = (prevCode < clearCode + 2 ? 1 : lengths[prevCode]) + 1;
                nextCode++;

                // Increase code size when table reaches the current code size limit
                if (nextCode >= (1 << codeSize) && codeSize < MaxCodeSize)
                {
                    codeSize++;
                }
            }

            prevCode = code;
        }

        // Drain any remaining sub-blocks so stream is positioned correctly for the next frame
        bitReader.DrainRemainingBlocks();

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data using LZW for GIF format. Writes sub-blocks to the output stream.
    /// </summary>
    public static void Compress(Stream stream, ReadOnlySpan<byte> data, int minCodeSize)
    {
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;

        // Hash table for string matching
        int tableCapacity = MaxTableSize * 2;
        int[] hashPrefix = new int[tableCapacity];
        byte[] hashSuffix = new byte[tableCapacity];
        int[] hashCode = new int[tableCapacity];

        void ResetTable()
        {
            Array.Fill(hashPrefix, -1);
            codeSize = minCodeSize + 1;
            nextCode = endCode + 1;
        }

        var bitWriter = new GifBitWriter(stream);

        // Emit clear code
        ResetTable();
        bitWriter.WriteBits(clearCode, codeSize);

        if (data.Length == 0)
        {
            bitWriter.WriteBits(endCode, codeSize);
            bitWriter.Flush();
            return;
        }

        int currentPrefix = data[0];
        for (int i = 1;i < data.Length;i++)
        {
            byte currentSuffix = data[i];

            // Look up (currentPrefix, currentSuffix) in hash table
            int hash = ((currentPrefix << 8) ^ currentSuffix) % tableCapacity;
            if (hash < 0)
            {
                hash += tableCapacity;
            }

            bool found = false;
            while (hashPrefix[hash] != -1)
            {
                if (hashPrefix[hash] == currentPrefix && hashSuffix[hash] == currentSuffix)
                {
                    currentPrefix = hashCode[hash];
                    found = true;
                    break;
                }
                hash = (hash + 1) % tableCapacity;
            }

            if (!found)
            {
                // Output current prefix code
                bitWriter.WriteBits(currentPrefix, codeSize);

                // Add new entry
                if (nextCode < MaxTableSize)
                {
                    hashPrefix[hash] = currentPrefix;
                    hashSuffix[hash] = currentSuffix;
                    hashCode[hash] = nextCode;
                    nextCode++;

                    // Encoder is 1 table entry ahead of decoder, so delay bump by 1
                    // using > instead of >= to stay in sync with decoder's >= threshold
                    if (nextCode > (1 << codeSize) && codeSize < MaxCodeSize)
                    {
                        codeSize++;
                    }
                }
                else
                {
                    // Table full: emit clear code and reset
                    bitWriter.WriteBits(clearCode, codeSize);
                    ResetTable();
                }

                currentPrefix = currentSuffix;
            }
        }

        // Output remaining prefix
        bitWriter.WriteBits(currentPrefix, codeSize);
        bitWriter.WriteBits(endCode, codeSize);
        bitWriter.Flush();
    }
}

/// <summary>
/// Reads variable-width codes from GIF sub-block structure. GIF sub-blocks: length byte (1-255) followed by that many
/// data bytes, terminated by 0x00.
/// </summary>
internal sealed class GifBitReader
{
    private readonly Stream stream;
    private byte[] blockData = new byte[256];
    private int blockSize;
    private int blockPos;
    private int bitBuffer;
    private int bitsInBuffer;
    private bool reachedBlockTerminator;

    public GifBitReader(Stream stream)
    {
        this.stream = stream;
    }

    public int ReadBits(int count)
    {
        while (bitsInBuffer < count)
        {
            int b = ReadNextByte();
            if (b < 0)
            {
                return -1;
            }

            bitBuffer |= b << bitsInBuffer;
            bitsInBuffer += 8;
        }

        int result = bitBuffer & ((1 << count) - 1);
        bitBuffer >>= count;
        bitsInBuffer -= count;
        return result;
    }

    private int ReadNextByte()
    {
        if (blockPos >= blockSize)
        {
            // Read next sub-block
            int size = stream.ReadByte();
            if (size <= 0)
            {
                reachedBlockTerminator = true;
                return -1; // End of sub-blocks (0x00 terminator)
            }

            blockSize = size;
            blockPos = 0;
            stream.ReadExactly(blockData.AsSpan(0, blockSize));
        }

        return blockData[blockPos++];
    }

    /// <summary>
    /// Drains any remaining sub-blocks after LZW decompression, ensuring the stream
    /// is positioned right after the 0x00 block terminator.
    /// </summary>
    public void DrainRemainingBlocks()
    {
        // If the block terminator (0x00) was already consumed by ReadNextByte, we're done
        if (reachedBlockTerminator)
            return;

        // Skip any remaining sub-blocks until the 0x00 terminator
        while (true)
        {
            int size = stream.ReadByte();
            if (size <= 0)
                break; // 0x00 terminator or end of stream

            // Skip this sub-block
            if (stream.CanSeek)
            {
                stream.Seek(size, SeekOrigin.Current);
            }
            else
            {
                byte[] skip = new byte[size];
                stream.ReadExactly(skip);
            }
        }
    }
}

/// <summary>
/// Writes variable-width codes as GIF sub-blocks.
/// </summary>
internal sealed class GifBitWriter
{
    private readonly Stream stream;
    private readonly byte[] blockBuffer = new byte[255];
    private int blockPos;
    private int bitBuffer;
    private int bitsInBuffer;

    public GifBitWriter(Stream stream)
    {
        this.stream = stream;
    }

    public void WriteBits(int value, int count)
    {
        bitBuffer |= value << bitsInBuffer;
        bitsInBuffer += count;

        while (bitsInBuffer >= 8)
        {
            OutputByte((byte)(bitBuffer & 0xFF));
            bitBuffer >>= 8;
            bitsInBuffer -= 8;
        }
    }

    public void Flush()
    {
        if (bitsInBuffer > 0)
        {
            OutputByte((byte)(bitBuffer & 0xFF));
        }

        // Write remaining block
        if (blockPos > 0)
        {
            stream.WriteByte((byte)blockPos);
            stream.Write(blockBuffer, 0, blockPos);
            blockPos = 0;
        }

        // Write block terminator
        stream.WriteByte(0);
    }

    private void OutputByte(byte b)
    {
        blockBuffer[blockPos++] = b;
        if (blockPos >= 255)
        {
            stream.WriteByte(255);
            stream.Write(blockBuffer, 0, 255);
            blockPos = 0;
        }
    }
}
