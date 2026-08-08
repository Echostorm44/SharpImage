// HEVC NAL Unit Parser with SIMD start code detection
// Builds on H.264 parser infrastructure but handles 2-byte HEVC NAL headers

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Hevc;

/// <summary>
/// High-performance parser for HEVC NAL units in AnnexB format.
/// Uses SIMD intrinsics for start code detection.
/// HEVC NAL units have a 2-byte header (vs H.264's 1 byte).
/// </summary>
public static class HevcNalParser
{
    private const int StartCodeLength3 = 3;  // 0x00 0x00 0x01
    private const int StartCodeLength4 = 4;  // 0x00 0x00 0x00 0x01
    private const int HevcNalHeaderSize = 2; // HEVC uses 2-byte NAL header

    /// <summary>
    /// Parses all NAL units from an AnnexB formatted HEVC byte stream.
    /// </summary>
    /// <param name="data">The input data containing NAL units with start code prefixes.</param>
    /// <param name="streamOffset">Base stream offset for position calculation.</param>
    /// <returns>Array of parsed HEVC NAL units.</returns>
    public static HevcNalUnit[] ParseAnnexB(ReadOnlySpan<byte> data, long streamOffset = 0)
    {
        if (data.Length < StartCodeLength3 + HevcNalHeaderSize)
            return [];

        // First pass: count NAL units
        int nalCount = CountStartCodes(data);
        if (nalCount == 0)
            return [];

        var nalUnits = new HevcNalUnit[nalCount];
        int nalIndex = 0;
        int startCodeLen = 0;

        // Find first start code
        int firstStart = FindNextStartCode(data, 0, out startCodeLen);
        if (firstStart < 0)
            return [];

        int currentNalStart = firstStart + startCodeLen;
        int currentStartCodeLen = startCodeLen;

        while (currentNalStart < data.Length && nalIndex < nalCount)
        {
            // Find next start code
            int nextStart = FindNextStartCode(data, currentNalStart, out startCodeLen);
            int nalEnd = nextStart >= 0 ? nextStart : data.Length;

            // Parse the NAL unit
            var nalData = data.Slice(currentNalStart, nalEnd - currentNalStart);
            if (nalData.Length >= HevcNalHeaderSize)
            {
                nalUnits[nalIndex++] = ParseNalHeader(nalData, currentNalStart + streamOffset);
            }

            if (nextStart < 0)
                break;

            currentNalStart = nextStart + startCodeLen;
            currentStartCodeLen = startCodeLen;
        }

        // Resize if we got fewer NAL units than expected
        if (nalIndex < nalCount)
            Array.Resize(ref nalUnits, nalIndex);

        return nalUnits;
    }

    /// <summary>
    /// Parses a single HEVC NAL unit from raw data (no start code prefix).
    /// </summary>
    public static HevcNalUnit ParseNalHeader(ReadOnlySpan<byte> data, long streamPosition = 0)
    {
        if (data.Length < HevcNalHeaderSize)
        {
            return new HevcNalUnit
            {
                Type = HevcNalUnitType.Unknown,
                LayerId = 0,
                TemporalIdPlus1 = 0,
                Payload = ReadOnlyMemory<byte>.Empty
            };
        }

        // HEVC NAL header structure (2 bytes):
        // Byte 0: forbidden_zero_bit (1) | nal_unit_type (6) | nuh_layer_id_msb (1)
        // Byte 1: nuh_layer_id_lsb (5) | nuh_temporal_id_plus1 (3)
        byte byte0 = data[0];
        byte byte1 = data[1];

        var nalType = HevcNalUtilities.GetNalType(byte0);
        byte layerId = HevcNalUtilities.GetLayerId(byte0, byte1);
        byte temporalIdPlus1 = HevcNalUtilities.GetTemporalIdPlus1(byte1);

        // Payload starts after 2-byte header
        var payload = data.Length > HevcNalHeaderSize 
            ? data.Slice(HevcNalHeaderSize).ToArray() 
            : Array.Empty<byte>();

        return new HevcNalUnit
        {
            Type = nalType,
            LayerId = layerId,
            TemporalIdPlus1 = temporalIdPlus1,
            Payload = payload
        };
    }

    /// <summary>
    /// Parses NAL units from length-prefixed format (e.g., hvcC/MP4).
    /// </summary>
    /// <param name="data">The input data with length-prefixed NAL units.</param>
    /// <param name="nalLengthSize">Size of the length prefix (1, 2, 3, or 4 bytes).</param>
    /// <returns>Array of parsed HEVC NAL units.</returns>
    public static HevcNalUnit[] ParseLengthPrefixed(ReadOnlySpan<byte> data, int nalLengthSize = 4)
    {
        if (data.Length < nalLengthSize + HevcNalHeaderSize)
            return [];

        var nalUnits = new System.Collections.Generic.List<HevcNalUnit>();
        int position = 0;

        while (position + nalLengthSize <= data.Length)
        {
            // Read NAL length
            int nalLength = nalLengthSize switch
            {
                1 => data[position],
                2 => (data[position] << 8) | data[position + 1],
                3 => (data[position] << 16) | (data[position + 1] << 8) | data[position + 2],
                4 => (data[position] << 24) | (data[position + 1] << 16) | 
                     (data[position + 2] << 8) | data[position + 3],
                _ => throw new ArgumentException("NAL length size must be 1-4", nameof(nalLengthSize))
            };

            position += nalLengthSize;

            if (position + nalLength > data.Length)
                break;

            var nalData = data.Slice(position, nalLength);
            if (nalData.Length >= HevcNalHeaderSize)
            {
                nalUnits.Add(ParseNalHeader(nalData, position));
            }

            position += nalLength;
        }

        return nalUnits.ToArray();
    }

    /// <summary>
    /// Removes emulation prevention bytes (0x03) from NAL payload to get RBSP.
    /// Same algorithm as H.264 since HEVC uses identical emulation prevention.
    /// </summary>
    public static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> nalPayload)
    {
        return RemoveEmulationPreventionBytes(nalPayload, out _);
    }

    /// <summary>
    /// Removes emulation prevention bytes (0x03) from NAL payload to get RBSP,
    /// and returns the NAL-byte positions of each removed byte.
    /// This mapping is needed to convert NAL entry_point_offsets to RBSP offsets.
    /// </summary>
    public static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> nalPayload, out int[] removedBytePositions)
    {
        if (nalPayload.Length < 3)
        {
            removedBytePositions = Array.Empty<int>();
            return nalPayload.ToArray();
        }

        // Worst case: same size as input
        byte[] rbsp = ArrayPool<byte>.Shared.Rent(nalPayload.Length);
        int rbspLength = 0;
        int zeroCount = 0;

        // Track positions of removed emulation prevention bytes (typically very few)
        int[] removedPositions = ArrayPool<int>.Shared.Rent(64);
        int removedCount = 0;

        try
        {
            for (int i = 0; i < nalPayload.Length; i++)
            {
                byte currentByte = nalPayload[i];

                if (zeroCount >= 2 && currentByte == 0x03)
                {
                    // Skip the emulation prevention byte
                    // But only if followed by 0x00, 0x01, 0x02, or 0x03
                    if (i + 1 < nalPayload.Length)
                    {
                        byte nextByte = nalPayload[i + 1];
                        if (nextByte <= 0x03)
                        {
                            if (removedCount >= removedPositions.Length)
                            {
                                var larger = ArrayPool<int>.Shared.Rent(removedPositions.Length * 2);
                                Array.Copy(removedPositions, larger, removedCount);
                                ArrayPool<int>.Shared.Return(removedPositions);
                                removedPositions = larger;
                            }
                            removedPositions[removedCount++] = i;
                            zeroCount = 0;
                            continue;
                        }
                    }
                    else
                    {
                        // 0x03 at end of stream - skip it
                        if (removedCount >= removedPositions.Length)
                        {
                            var larger = ArrayPool<int>.Shared.Rent(removedPositions.Length * 2);
                            Array.Copy(removedPositions, larger, removedCount);
                            ArrayPool<int>.Shared.Return(removedPositions);
                            removedPositions = larger;
                        }
                        removedPositions[removedCount++] = i;
                        zeroCount = 0;
                        continue;
                    }
                }

                if (currentByte == 0x00)
                    zeroCount++;
                else
                    zeroCount = 0;

                rbsp[rbspLength++] = currentByte;
            }

            byte[] result = new byte[rbspLength];
            Array.Copy(rbsp, result, rbspLength);

            removedBytePositions = new int[removedCount];
            Array.Copy(removedPositions, removedBytePositions, removedCount);

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rbsp);
            ArrayPool<int>.Shared.Return(removedPositions);
        }
    }

    /// <summary>
    /// Counts start codes in the data for pre-allocation.
    /// </summary>
    private static int CountStartCodes(ReadOnlySpan<byte> data)
    {
        int count = 0;
        int position = 0;

        while (position < data.Length - 2)
        {
            int nextStart = FindNextStartCode(data, position, out int startCodeLen);
            if (nextStart < 0)
                break;

            count++;
            position = nextStart + startCodeLen;
        }

        return count;
    }

    /// <summary>
    /// Finds the next start code (0x00 0x00 0x01 or 0x00 0x00 0x00 0x01).
    /// Uses SIMD when available for faster searching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindNextStartCode(ReadOnlySpan<byte> data, int startPosition, out int startCodeLength)
    {
        startCodeLength = 0;

        if (startPosition >= data.Length - 2)
            return -1;

        // Use SIMD for larger buffers
        if (Avx2.IsSupported && data.Length - startPosition >= 32)
        {
            int result = FindStartCodeAvx2(data, startPosition, out startCodeLength);
            if (result >= 0)
                return result;
        }
        else if (Sse2.IsSupported && data.Length - startPosition >= 16)
        {
            int result = FindStartCodeSse2(data, startPosition, out startCodeLength);
            if (result >= 0)
                return result;
        }

        // Scalar fallback
        return FindStartCodeScalar(data, startPosition, out startCodeLength);
    }

    private static int FindStartCodeScalar(ReadOnlySpan<byte> data, int startPosition, out int startCodeLength)
    {
        startCodeLength = 0;

        for (int i = startPosition; i < data.Length - 2; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    // Check for 4-byte start code
                    if (i > 0 && data[i - 1] == 0)
                    {
                        startCodeLength = 4;
                        return i - 1;
                    }
                    startCodeLength = 3;
                    return i;
                }
                else if (data[i + 2] == 0 && i + 3 < data.Length && data[i + 3] == 1)
                {
                    startCodeLength = 4;
                    return i;
                }
            }
        }

        return -1;
    }

    private static unsafe int FindStartCodeAvx2(ReadOnlySpan<byte> data, int startPosition, out int startCodeLength)
    {
        startCodeLength = 0;
        Vector256<byte> zeros = Vector256<byte>.Zero;
        Vector256<byte> ones = Vector256.Create((byte)1);

        fixed (byte* ptr = data)
        {
            int i = startPosition;
            int vectorEnd = data.Length - 34;

            while (i < vectorEnd)
            {
                // Load 32 bytes at current position
                var current = Avx.LoadVector256(ptr + i);
                var next1 = Avx.LoadVector256(ptr + i + 1);
                var next2 = Avx.LoadVector256(ptr + i + 2);

                // Compare for 0x00 0x00 0x01 pattern
                var isZero0 = Avx2.CompareEqual(current, zeros);
                var isZero1 = Avx2.CompareEqual(next1, zeros);
                var isOne2 = Avx2.CompareEqual(next2, ones);

                var match = Avx2.And(Avx2.And(isZero0, isZero1), isOne2);
                int mask = Avx2.MoveMask(match);

                if (mask != 0)
                {
                    int offset = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
                    int position = i + offset;

                    // Check for 4-byte start code
                    if (position > 0 && data[position - 1] == 0)
                    {
                        startCodeLength = 4;
                        return position - 1;
                    }
                    startCodeLength = 3;
                    return position;
                }

                i += 32;
            }
        }

        // Fall back to scalar for remaining bytes
        return FindStartCodeScalar(data, startPosition < data.Length - 34 ? data.Length - 34 : startPosition, out startCodeLength);
    }

    private static unsafe int FindStartCodeSse2(ReadOnlySpan<byte> data, int startPosition, out int startCodeLength)
    {
        startCodeLength = 0;
        Vector128<byte> zeros = Vector128<byte>.Zero;
        Vector128<byte> ones = Vector128.Create((byte)1);

        fixed (byte* ptr = data)
        {
            int i = startPosition;
            int vectorEnd = data.Length - 18;

            while (i < vectorEnd)
            {
                var current = Sse2.LoadVector128(ptr + i);
                var next1 = Sse2.LoadVector128(ptr + i + 1);
                var next2 = Sse2.LoadVector128(ptr + i + 2);

                var isZero0 = Sse2.CompareEqual(current, zeros);
                var isZero1 = Sse2.CompareEqual(next1, zeros);
                var isOne2 = Sse2.CompareEqual(next2, ones);

                var match = Sse2.And(Sse2.And(isZero0, isZero1), isOne2);
                int mask = Sse2.MoveMask(match);

                if (mask != 0)
                {
                    int offset = System.Numerics.BitOperations.TrailingZeroCount((uint)mask);
                    int position = i + offset;

                    if (position > 0 && data[position - 1] == 0)
                    {
                        startCodeLength = 4;
                        return position - 1;
                    }
                    startCodeLength = 3;
                    return position;
                }

                i += 16;
            }
        }

        return FindStartCodeScalar(data, startPosition < data.Length - 18 ? data.Length - 18 : startPosition, out startCodeLength);
    }

    /// <summary>
    /// Extracts specific NAL unit types from a stream.
    /// </summary>
    public static HevcNalUnit[] FilterByType(HevcNalUnit[] nalUnits, params HevcNalUnitType[] types)
    {
        var typeSet = new System.Collections.Generic.HashSet<HevcNalUnitType>(types);
        return Array.FindAll(nalUnits, n => typeSet.Contains(n.Type));
    }

    /// <summary>
    /// Finds the first VPS in the NAL unit array.
    /// </summary>
    public static HevcNalUnit? FindVps(HevcNalUnit[] nalUnits)
    {
        foreach (var nal in nalUnits)
        {
            if (nal.Type == HevcNalUnitType.VideoParameterSet)
                return nal;
        }
        return null;
    }

    /// <summary>
    /// Finds the first SPS in the NAL unit array.
    /// </summary>
    public static HevcNalUnit? FindSps(HevcNalUnit[] nalUnits)
    {
        foreach (var nal in nalUnits)
        {
            if (nal.Type == HevcNalUnitType.SequenceParameterSet)
                return nal;
        }
        return null;
    }

    /// <summary>
    /// Finds the first PPS in the NAL unit array.
    /// </summary>
    public static HevcNalUnit? FindPps(HevcNalUnit[] nalUnits)
    {
        foreach (var nal in nalUnits)
        {
            if (nal.Type == HevcNalUnitType.PictureParameterSet)
                return nal;
        }
        return null;
    }
}
