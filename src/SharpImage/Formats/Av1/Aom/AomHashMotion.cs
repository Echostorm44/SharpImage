using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

/// <summary>block_hash: a candidate block's position and its full 32-bit hash.</summary>
internal struct AomBlockHash
{
    public short X, Y;
    public uint HashValue2;
}

/// <summary>IntraBCHashInfo: the frame's intrabc hash table (hash_table: 6 &lt;&lt; 16 buckets of block_hash vectors,
/// at most 256 entries each) and the per-block hash buffers.</summary>
internal sealed class AomIntrabcHashInfo
{
    public const int SrcBits = 16, MaxAddr = 6 << SrcBits, MaxCandidatesPerHashBucket = 256;
    public const int BufferSizeForBlockHash = 128 * 128;   // AOM_BUFFER_SIZE_FOR_BLOCK_HASH (MAX_SB_SIZE^2)
    public List<AomBlockHash>?[] LookupTable = new List<AomBlockHash>?[MaxAddr];
    public readonly uint[][] HashValueBuffer = { new uint[BufferSizeForBlockHash], new uint[BufferSizeForBlockHash] };
}

// Port of libaom 3.14.1 av1/encoder/hash.c (CRC-32C) and hash_motion.c (8-bit): the 2x2 identity hashes, the
// hierarchical CRC-32C block hashes of the source frame, the hash table fill in libaom's exploration order, the table
// count / iteration, and av1_get_block_hash_value for the current block.
internal static class AomHashMotion
{
    private static readonly uint[] Crc32cTable = BuildCrc32cTable();

    private static uint[] BuildCrc32cTable()
    {
        const uint Poly = 0x82f63b78;
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint crc = n;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ Poly : crc >> 1;
            t[n] = crc;
        }
        return t;
    }

    /// <summary>av1_get_crc32c_value (the SSE4.2 kernel libaom dispatches computes the same CRC-32C) of four
    /// little-endian uint32 values.</summary>
    public static uint Crc32c4(uint a, uint b, uint c, uint d)
    {
        if (Sse42.X64.IsSupported)
        {
            ulong crc = 0xFFFFFFFF;
            crc = Sse42.X64.Crc32(crc, a | ((ulong)b << 32));
            crc = Sse42.X64.Crc32(crc, c | ((ulong)d << 32));
            return (uint)crc ^ 0xFFFFFFFF;
        }
        uint r = 0xFFFFFFFF;
        r = Crc32cWord(r, a); r = Crc32cWord(r, b); r = Crc32cWord(r, c); r = Crc32cWord(r, d);
        return r ^ 0xFFFFFFFF;
    }

    private static uint Crc32cWord(uint crc, uint w)
    {
        for (int i = 0; i < 4; i++) crc = Crc32cTable[(crc ^ (w >> (8 * i))) & 0xff] ^ (crc >> 8);
        return crc;
    }

    /// <summary>av1_get_crc32c_value on arbitrary bytes (table-driven; equal to the hardware CRC-32C).</summary>
    public static uint Crc32c(ReadOnlySpan<byte> buf)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte v in buf) crc = Crc32cTable[(crc ^ v) & 0xff] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>hash_block_size_to_index.</summary>
    private static int HashBlockSizeToIndex(int blockSize) => blockSize switch
    {
        4 => 0, 8 => 1, 16 => 2, 32 => 3, 64 => 4, 128 => 5, _ => -1,
    };

    /// <summary>get_identity_hash_value.</summary>
    private static uint IdentityHash(byte a, byte b, byte c, byte d) => ((uint)a << 24) + ((uint)b << 16) + ((uint)c << 8) + d;

    /// <summary>av1_generate_block_2x2_hash_value (8-bit) over the luma crop.</summary>
    public static void GenerateBlock2x2HashValue(byte[] y, int yOff, int stride, int cropW, int cropH, uint[] picBlockHash)
    {
        int xEnd = cropW - 2 + 1, yEnd = cropH - 2 + 1;
        int pos = 0;
        for (int yPos = 0; yPos < yEnd; yPos++)
        {
            for (int xPos = 0; xPos < xEnd; xPos++)
            {
                int p = yOff + yPos * stride + xPos;
                picBlockHash[pos] = IdentityHash(y[p], y[p + 1], y[p + stride], y[p + stride + 1]);
                pos++;
            }
            pos += 2 - 1;
        }
    }

    /// <summary>av1_generate_block_hash_value: the block_size hashes from four block_size / 2 hashes.</summary>
    public static void GenerateBlockHashValue(int picWidth, int picHeight, int blockSize, uint[] src, uint[] dst)
    {
        int xEnd = picWidth - blockSize + 1, yEnd = picHeight - blockSize + 1;
        int srcSize = blockSize >> 1;
        int pos = 0;
        for (int yPos = 0; yPos < yEnd; yPos++)
        {
            for (int xPos = 0; xPos < xEnd; xPos++)
            {
                dst[pos] = Crc32c4(src[pos], src[pos + srcSize], src[pos + srcSize * picWidth], src[pos + srcSize * picWidth + srcSize]);
                pos++;
            }
            pos += blockSize - 1;
        }
    }

    // hash_table_add_to_table
    private static void AddToTable(AomIntrabcHashInfo t, uint hashValue, AomBlockHash h)
    {
        var v = t.LookupTable[hashValue] ??= new List<AomBlockHash>(10);
        if (v.Count < AomIntrabcHashInfo.MaxCandidatesPerHashBucket) v.Add(h);
    }

    /// <summary>av1_add_to_hash_map_by_row_with_precal_data (the hierarchical exploration order).</summary>
    public static void AddToHashMapByRowWithPrecalData(AomIntrabcHashInfo t, uint[] picHash, int picWidth, int picHeight, int blockSize)
    {
        int xEnd = picWidth - blockSize + 1, yEnd = picHeight - blockSize + 1;
        int addValue = HashBlockSizeToIndex(blockSize) << AomIntrabcHashInfo.SrcBits;
        const int crcMask = (1 << AomIntrabcHashInfo.SrcBits) - 1;
        int step = blockSize, xOffset = 0, yOffset = 0;
        while (step > 1)
        {
            for (int xPos = xOffset; xPos < xEnd; xPos += step)
                for (int yPos = yOffset; yPos < yEnd; yPos += step)
                {
                    int pos = yPos * picWidth + xPos;
                    uint hashValue1 = (uint)((picHash[pos] & crcMask) + addValue);
                    AddToTable(t, hashValue1, new AomBlockHash { X = (short)xPos, Y = (short)yPos, HashValue2 = picHash[pos] });
                }
            if (xOffset == 0 && yOffset == 0) xOffset = step / 2;
            else if (xOffset == step / 2 && yOffset == 0) { xOffset = 0; yOffset = step / 2; }
            else if (xOffset == 0 && yOffset == step / 2) xOffset = step / 2;
            else { step /= 2; xOffset = step / 2; yOffset = 0; }
        }
    }

    /// <summary>av1_hash_table_count.</summary>
    public static int Count(AomIntrabcHashInfo t, uint hashValue) => t.LookupTable[hashValue]?.Count ?? 0;

    /// <summary>The frame-level table build of encode_frame_internal (av1_use_hash_me): 2x2 hashes of the source, then
    /// each block size from 4 up to the superblock (8 with hash_max_8x8_intrabc_blocks), sizes from minAllocSize on
    /// added to the table.</summary>
    public static AomIntrabcHashInfo BuildFrameTable(AomFrameBuffer source, int mibSizeLog2, bool hashMax8x8, int minAllocSize)
    {
        var info = new AomIntrabcHashInfo();
        int picWidth = source.CropWidths[0], picHeight = source.CropHeights[0];
        var buf = new[] { new uint[picWidth * picHeight], new uint[picWidth * picHeight] };
        GenerateBlock2x2HashValue(source.Buffers[0], source.Offsets[0], source.Strides[0], picWidth, picHeight, buf[0]);
        int maxSbSize = 1 << (mibSizeLog2 + 2);
        if (hashMax8x8) maxSbSize = Math.Min(8, maxSbSize);
        int srcIdx = 0;
        for (int size = 4; size <= maxSbSize; size *= 2, srcIdx ^= 1)
        {
            int dstIdx = srcIdx ^ 1;
            GenerateBlockHashValue(picWidth, picHeight, size, buf[srcIdx], buf[dstIdx]);
            if (size >= minAllocSize) AddToHashMapByRowWithPrecalData(info, buf[dstIdx], picWidth, picHeight, size);
        }
        return info;
    }

    /// <summary>av1_get_block_hash_value (8-bit) of a square block.</summary>
    public static void GetBlockHashValue(AomIntrabcHashInfo info, byte[] y, int off, int stride, int blockSize, out uint hashValue1,
        out uint hashValue2)
    {
        int addValue = HashBlockSizeToIndex(blockSize) << AomIntrabcHashInfo.SrcBits;
        const int crcMask = (1 << AomIntrabcHashInfo.SrcBits) - 1;
        var buf = info.HashValueBuffer;
        int subBlockInWidth = blockSize >> 1;
        for (int yPos = 0; yPos < blockSize; yPos += 2)
            for (int xPos = 0; xPos < blockSize; xPos += 2)
            {
                int pos = (yPos >> 1) * subBlockInWidth + (xPos >> 1);
                int p = off + yPos * stride + xPos;
                buf[0][pos] = IdentityHash(y[p], y[p + 1], y[p + stride], y[p + stride + 1]);
            }
        int srcSubBlockInWidth = subBlockInWidth;
        subBlockInWidth >>= 1;
        int srcIdx = 0, dstIdx = 1;
        for (int subWidth = 4; subWidth <= blockSize; subWidth *= 2, srcIdx ^= 1)
        {
            dstIdx = srcIdx ^ 1;
            int dstPos = 0;
            for (int yPos = 0; yPos < subBlockInWidth; yPos++)
                for (int xPos = 0; xPos < subBlockInWidth; xPos++)
                {
                    int srcPos = (yPos << 1) * srcSubBlockInWidth + (xPos << 1);
                    buf[dstIdx][dstPos] = Crc32c4(buf[srcIdx][srcPos], buf[srcIdx][srcPos + 1], buf[srcIdx][srcPos + srcSubBlockInWidth],
                        buf[srcIdx][srcPos + srcSubBlockInWidth + 1]);
                    dstPos++;
                }
            srcSubBlockInWidth = subBlockInWidth;
            subBlockInWidth >>= 1;
        }
        hashValue1 = (uint)((buf[dstIdx][0] & crcMask) + addValue);
        hashValue2 = buf[dstIdx][0];
    }
}
