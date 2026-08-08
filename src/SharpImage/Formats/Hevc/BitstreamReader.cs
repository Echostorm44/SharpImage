using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// Bitstream reader for H.264 NAL unit parsing.
    /// Supports exp-golomb codes and bit-level access required for SPS/PPS parsing.
    /// </summary>
    public ref struct BitstreamReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int bytePosition;
        private int bitPosition;  // 0-7, where 0 is MSB

        /// <summary>Gets the total number of bits in the stream.</summary>
        public readonly int TotalBits => data.Length * 8;

        /// <summary>Gets the current bit position in the stream.</summary>
        public readonly int BitPosition => bytePosition * 8 + bitPosition;

        /// <summary>Gets the number of remaining bits.</summary>
        public readonly int RemainingBits => TotalBits - BitPosition;

        /// <summary>Returns true if more bits are available.</summary>
        public readonly bool HasMoreBits => bytePosition < data.Length;

        /// <summary>
        /// Creates a new bitstream reader for the given data.
        /// </summary>
        /// <param name="data">The byte data to read from.</param>
        public BitstreamReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            bytePosition = 0;
            bitPosition = 0;
        }

        /// <summary>
        /// Reads a single bit from the stream.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBit()
        {
            if (bytePosition >= data.Length)
                throw new InvalidOperationException("Attempted to read past end of bitstream");

            uint bit = (uint)((data[bytePosition] >> (7 - bitPosition)) & 1);

            bitPosition++;
            if (bitPosition >= 8)
            {
                bitPosition = 0;
                bytePosition++;
            }

            return bit;
        }

        /// <summary>
        /// Reads up to 32 bits from the stream.
        /// </summary>
        /// <param name="numberOfBits">Number of bits to read (1-32).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int numberOfBits)
        {
            if (numberOfBits <= 0 || numberOfBits > 32)
                throw new ArgumentOutOfRangeException(nameof(numberOfBits), "Must be 1-32");

            if (RemainingBits < numberOfBits)
                throw new InvalidOperationException("Attempted to read past end of bitstream");

            uint result = 0;

            // Optimize for byte-aligned reads
            if (bitPosition == 0 && numberOfBits >= 8)
            {
                while (numberOfBits >= 8)
                {
                    result = (result << 8) | data[bytePosition++];
                    numberOfBits -= 8;
                }
            }

            // Read remaining bits
            for (int i = 0; i < numberOfBits; i++)
            {
                result = (result << 1) | ReadBit();
            }

            return result;
        }

        /// <summary>
        /// Reads a signed 32-bit value from the stream.
        /// </summary>
        public int ReadBitsSigned(int numberOfBits)
        {
            uint value = ReadBits(numberOfBits);
            
            // Sign extend
            int shift = 32 - numberOfBits;
            return (int)(value << shift) >> shift;
        }

        /// <summary>
        /// Reads up to 64 bits from the stream.
        /// </summary>
        /// <param name="numberOfBits">Number of bits to read (1-64).</param>
        public ulong ReadBits64(int numberOfBits)
        {
            if (numberOfBits <= 0 || numberOfBits > 64)
                throw new ArgumentOutOfRangeException(nameof(numberOfBits), "Must be 1-64");

            if (numberOfBits <= 32)
                return ReadBits(numberOfBits);

            // Read high bits then low bits
            int highBits = numberOfBits - 32;
            ulong high = ReadBits(highBits);
            ulong low = ReadBits(32);
            return (high << 32) | low;
        }

        /// <summary>
        /// Reads an unsigned exp-golomb coded value (ue(v) in the spec).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadExpGolombUnsigned()
        {
            // Count leading zeros
            int leadingZeroBits = 0;
            while (ReadBit() == 0)
            {
                leadingZeroBits++;
                if (leadingZeroBits > 31)
                    throw new InvalidOperationException("Invalid exp-golomb code");
            }

            if (leadingZeroBits == 0)
                return 0;

            // Read the suffix
            uint suffix = ReadBits(leadingZeroBits);
            return (1u << leadingZeroBits) - 1 + suffix;
        }

        /// <summary>
        /// Reads a signed exp-golomb coded value (se(v) in the spec).
        /// Maps 0, 1, 2, 3, 4... to 0, 1, -1, 2, -2...
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadExpGolombSigned()
        {
            uint codeNum = ReadExpGolombUnsigned();
            
            // Convert to signed: ceil(codeNum/2) with alternating sign
            int value = (int)((codeNum + 1) >> 1);
            return ((codeNum & 1) == 0) ? -value : value;
        }

        /// <summary>
        /// Skips the specified number of bits.
        /// </summary>
        public void Skip(int numberOfBits)
        {
            if (numberOfBits <= 0)
                return;

            int totalBitsAfterSkip = BitPosition + numberOfBits;
            if (totalBitsAfterSkip > TotalBits)
                throw new InvalidOperationException("Attempted to skip past end of bitstream");

            bytePosition = totalBitsAfterSkip / 8;
            bitPosition = totalBitsAfterSkip % 8;
        }

        /// <summary>
        /// Skips the specified number of bits. Alias for Skip().
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SkipBits(int numberOfBits) => Skip(numberOfBits);

        /// <summary>
        /// Aligns the read position to the next byte boundary.
        /// </summary>
        public void AlignToByte()
        {
            if (bitPosition != 0)
            {
                bitPosition = 0;
                bytePosition++;
            }
        }

        /// <summary>
        /// Reads the rbsp_trailing_bits() syntax element.
        /// Used to verify correct parsing alignment.
        /// </summary>
        public bool ReadTrailingBits()
        {
            if (!HasMoreBits)
                return true;

            // First bit should be 1
            if (ReadBit() != 1)
                return false;

            // Remaining bits to byte boundary should be 0
            while (bitPosition != 0 && HasMoreBits)
            {
                if (ReadBit() != 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if there is more RBSP data (more_rbsp_data() in spec).
        /// </summary>
        public readonly bool HasMoreRbspData()
        {
            if (!HasMoreBits)
                return false;

            // Save position and check for trailing bits
            int savedBytePos = bytePosition;
            int savedBitPos = bitPosition;

            // Find the last 1 bit
            int lastOneBit = -1;
            int tempBytePos = bytePosition;
            int tempBitPos = bitPosition;

            while (tempBytePos < data.Length)
            {
                byte b = data[tempBytePos];
                for (int i = (tempBytePos == bytePosition) ? bitPosition : 0; i < 8; i++)
                {
                    if (((b >> (7 - i)) & 1) == 1)
                        lastOneBit = tempBytePos * 8 + i;
                }
                tempBytePos++;
            }

            // If the last 1 bit is the rbsp_stop_one_bit followed by zeros, no more data
            return lastOneBit > BitPosition;
        }

        /// <summary>
        /// Reads a scaling list as used in SPS/PPS.
        /// </summary>
        /// <param name="scalingList">Output array for the scaling list.</param>
        /// <param name="sizeOfScalingList">Size of the scaling list (16 or 64).</param>
        /// <param name="useDefaultScalingMatrix">Output: true if default matrix should be used.</param>
        public void ReadScalingList(int[] scalingList, int sizeOfScalingList, out bool useDefaultScalingMatrix)
        {
            useDefaultScalingMatrix = false;
            int lastScale = 8;
            int nextScale = 8;

            for (int j = 0; j < sizeOfScalingList; j++)
            {
                if (nextScale != 0)
                {
                    int deltaScale = ReadExpGolombSigned();
                    nextScale = (lastScale + deltaScale + 256) % 256;
                    useDefaultScalingMatrix = (j == 0 && nextScale == 0);
                }
                scalingList[j] = (nextScale == 0) ? lastScale : nextScale;
                lastScale = scalingList[j];
            }
        }

        /// <summary>
        /// Peeks at the next bit without advancing the position.
        /// </summary>
        public readonly uint PeekBit()
        {
            if (bytePosition >= data.Length)
                return 0;

            return (uint)((data[bytePosition] >> (7 - bitPosition)) & 1);
        }

        /// <summary>
        /// Peeks at the next N bits without advancing the position.
        /// When fewer bits remain than requested, reads available bits and zero-pads the LSBs.
        /// </summary>
        public readonly uint PeekBits(int numberOfBits)
        {
            if (numberOfBits <= 0 || numberOfBits > 32)
                throw new ArgumentOutOfRangeException(nameof(numberOfBits));

            int remaining = RemainingBits;
            if (remaining <= 0)
                return 0;

            var tempReader = this;
            if (remaining < numberOfBits)
            {
                // Read available bits and zero-pad the LSBs
                uint val = tempReader.ReadBits(remaining);
                return val << (numberOfBits - remaining);
            }

            return tempReader.ReadBits(numberOfBits);
        }
    }
}
