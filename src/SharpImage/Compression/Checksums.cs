using System.Runtime.CompilerServices;

namespace SharpImage.Compression;

/// <summary>
/// CRC-32 checksum computation per ISO 3309 / ITU-T V.42. Used by PNG for chunk CRC validation. Pure C# table-based
/// implementation with no external dependencies.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0;i < 256;i++)
        {
            uint crc = i;
            for (int j = 0;j < 8;j++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes CRC-32 over a span of bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0;i < data.Length;i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Continues CRC-32 computation (for incremental use).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc ^= 0xFFFFFFFF;
        for (int i = 0;i < data.Length;i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }
}

/// <summary>
/// Adler-32 checksum computation. Used by zlib format for data integrity.
/// </summary>
public static class Adler32
{
    private const uint ModAdler = 65521;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        for (int i = 0;i < data.Length;i++)
        {
            a = (a + data[i]) % ModAdler;
            b = (b + a) % ModAdler;
        }
        return (b << 16) | a;
    }
}
