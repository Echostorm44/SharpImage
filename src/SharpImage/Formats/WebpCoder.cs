using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

/// <summary>
/// WebP format reader and writer. Supports VP8L (lossless) and VP8 (lossy). Pure C# implementation following Google's
/// WebP specification (RFC 9649) and VP8 Data Format (RFC 6386).
/// </summary>
public static class WebpCoder
{
    /// <summary>
    /// Detect WebP format by RIFF+WEBP signature.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= 12 &&
        data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
        data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P';

    private const byte Vp8lSignatureByte = 0x2F;
    private const int MaxWebpDimension = 16384;
    private const uint ColorCacheHashMul = 0x1E35A7BD;

    private static readonly int[] CodeLengthCodeOrder = [ 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 ];

    // VP8L LZ77 2D distance map (120 entries): (dx, dy) offsets
    private static readonly sbyte[] DistMapDx = [ 0, 1, 1, -1, 0, 2, 1, -1, 2, -2, 2, -2, 0, 3, 1, -1, 3, -3, 2, -2, 3, -3, 0, 4, 1, -1, 4, -4, 3, -3, 2, -2, 4, -4, 0, 3, -3, 4, -4, 5, 1, -1, 5, -5, 2, -2, 5, -5, 4, -4, 3, -3, 5, -5, 0, 6, 1, -1, 6, -6, 2, -2, 6, -6, 4, -4, 5, -5, 3, -3, 6, -6, 0, 7, 1, -1, 5, -5, 7, -7, 4, -4, 6, -6, 2, -2, 7, -7, 3, -3, 7, -7, 5, -5, 6, -6, 8, 4, -4, 7, -7, 8, 8, 6, -6, 8, 5, -5, 7, -7, 8, 6, -6, 7, -7, 8, 7, -7, 8, 8 ];
    private static readonly sbyte[] DistMapDy = [ 1, 0, 1, 1, 2, 0, 2, 2, 1, 1, 2, 2, 3, 0, 3, 3, 1, 1, 3, 3, 2, 2, 4, 0, 4, 4, 1, 1, 3, 3, 4, 4, 2, 2, 5, 4, 4, 3, 3, 0, 5, 5, 1, 1, 5, 5, 2, 2, 4, 4, 5, 5, 3, 3, 6, 0, 6, 6, 1, 1, 6, 6, 2, 2, 5, 5, 4, 4, 6, 6, 3, 3, 7, 0, 7, 7, 5, 5, 1, 1, 6, 6, 4, 4, 7, 7, 2, 2, 7, 7, 3, 3, 6, 6, 5, 5, 0, 7, 7, 4, 4, 1, 2, 6, 6, 3, 7, 7, 5, 5, 4, 7, 7, 6, 6, 5, 7, 7, 6, 7 ];

    // ═══════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════

    public static ImageFrame Read(Stream stream)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        if (data.Length < 12)
        {
            throw new InvalidDataException("WebP file too small.");
        }

        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
        {
            throw new InvalidDataException("Missing RIFF signature.");
        }

        if (data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
        {
            throw new InvalidDataException("Missing WEBP signature.");
        }

        int offset = 12;
        byte[]? vp8lData = null;
        byte[]? vp8Data = null;
        byte[]? alphData = null;

        while (offset + 8 <= data.Length)
        {
            uint chunkSize = BitConverter.ToUInt32(data, offset + 4);
            int safeSize = (int)Math.Min(chunkSize, data.Length - offset - 8);
            int chunkStart = offset + 8;
            byte c0 = data[offset], c1 = data[offset + 1], c2 = data[offset + 2], c3 = data[offset + 3];

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'L')
            {
                vp8lData = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, vp8lData, 0, safeSize);
            }
            else if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == ' ')
            {
                vp8Data = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, vp8Data, 0, safeSize);
            }
            else if (c0 == 'A' && c1 == 'L' && c2 == 'P' && c3 == 'H')
            {
                alphData = new byte[safeSize];
                Buffer.BlockCopy(data, chunkStart, alphData, 0, safeSize);
            }

            offset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - offset));
            if ((chunkSize & 1) != 0)
            {
                offset++;
            }
        }

        if (vp8lData != null)
        {
            return DecodeVp8l(vp8lData);
        }

        if (vp8Data != null)
        {
            return DecodeVp8Lossy(vp8Data, alphData);
        }

        throw new InvalidDataException("No VP8 or VP8L chunk found in WebP file.");
    }

    /// <summary>
    /// Writes an image as WebP. quality >= 100 produces lossless (VP8L), otherwise lossy (VP8).
    /// </summary>
    public static void Write(ImageFrame image, Stream stream, int quality = 100)
    {
        if (quality >= 100)
        {
            WriteLossless(image, stream);
        }
        else
        {
            WriteLossy(image, stream, quality);
        }
    }

    /// <summary>
    /// Read an animated WebP file into an ImageSequence.
    /// If the file is a single-frame WebP, returns a sequence with one frame.
    /// </summary>
    public static ImageSequence ReadSequence(Stream stream)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        if (data.Length < 12 ||
            data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
            data[8] != 'W' || data[9] != 'E' || data[10] != 'B' || data[11] != 'P')
            throw new InvalidDataException("Invalid WebP file.");

        var sequence = new ImageSequence { FormatName = "WEBP" };
        int offset = 12;
        bool isAnimation = false;
        int canvasWidth = 0, canvasHeight = 0;

        // First pass: look for VP8X to detect animation
        int firstPassOffset = 12;
        while (firstPassOffset + 8 <= data.Length)
        {
            byte c0 = data[firstPassOffset], c1 = data[firstPassOffset + 1];
            byte c2 = data[firstPassOffset + 2], c3 = data[firstPassOffset + 3];
            uint chunkSize = BitConverter.ToUInt32(data, firstPassOffset + 4);
            int safeSize = (int)Math.Min(chunkSize, data.Length - firstPassOffset - 8);

            if (c0 == 'V' && c1 == 'P' && c2 == '8' && c3 == 'X' && safeSize >= 10)
            {
                byte flags = data[firstPassOffset + 8];
                isAnimation = (flags & 0x02) != 0;
                canvasWidth = Read24LE(data, firstPassOffset + 12) + 1;
                canvasHeight = Read24LE(data, firstPassOffset + 15) + 1;
                break;
            }

            firstPassOffset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - firstPassOffset));
            if ((chunkSize & 1) != 0) firstPassOffset++;
        }

        if (!isAnimation)
        {
            // Not animated — decode as single frame
            var frame = Read(new MemoryStream(data));
            frame.Delay = 0;
            sequence.AddFrame(frame);
            sequence.CanvasWidth = (int)frame.Columns;
            sequence.CanvasHeight = (int)frame.Rows;
            return sequence;
        }

        sequence.CanvasWidth = canvasWidth;
        sequence.CanvasHeight = canvasHeight;

        // Parse ANIM and ANMF chunks
        offset = 12;
        while (offset + 8 <= data.Length)
        {
            byte c0 = data[offset], c1 = data[offset + 1], c2 = data[offset + 2], c3 = data[offset + 3];
            uint chunkSize = BitConverter.ToUInt32(data, offset + 4);
            int safeSize = (int)Math.Min(chunkSize, data.Length - offset - 8);
            int chunkStart = offset + 8;

            if (c0 == 'A' && c1 == 'N' && c2 == 'I' && c3 == 'M' && safeSize >= 6)
            {
                sequence.LoopCount = BitConverter.ToUInt16(data, chunkStart + 4);
            }
            else if (c0 == 'A' && c1 == 'N' && c2 == 'M' && c3 == 'F' && safeSize >= 16)
            {
                int frameX = Read24LE(data, chunkStart) * 2;
                int frameY = Read24LE(data, chunkStart + 3) * 2;
                int frameWidth = Read24LE(data, chunkStart + 6) + 1;
                int frameHeight = Read24LE(data, chunkStart + 9) + 1;
                int durationMs = Read24LE(data, chunkStart + 12);
                byte frameFlags = data[chunkStart + 15];
                // bit 1 = blending (0 = alpha blend, 1 = don't blend)
                // bit 0 = disposal (0 = don't dispose, 1 = dispose to background)
                bool disposeToBackground = (frameFlags & 0x01) != 0;

                // The frame data starts at chunkStart + 16 and contains a VP8/VP8L sub-chunk
                int subOffset = chunkStart + 16;
                int subRemaining = safeSize - 16;

                ImageFrame? frame = null;
                if (subRemaining >= 8)
                {
                    byte s0 = data[subOffset], s1 = data[subOffset + 1], s2 = data[subOffset + 2], s3 = data[subOffset + 3];
                    uint subSize = BitConverter.ToUInt32(data, subOffset + 4);
                    int subDataSize = (int)Math.Min(subSize, subRemaining - 8);
                    byte[] subData = new byte[subDataSize];
                    Buffer.BlockCopy(data, subOffset + 8, subData, 0, subDataSize);

                    if (s0 == 'V' && s1 == 'P' && s2 == '8' && s3 == 'L')
                    {
                        frame = DecodeVp8l(subData);
                    }
                    else if (s0 == 'V' && s1 == 'P' && s2 == '8' && s3 == ' ')
                    {
                        // Check for ALPH chunk inside ANMF (may precede VP8)
                        byte[]? alphData = null;
                        // VP8 lossy frames in ANMF may have an ALPH chunk before VP8
                        // Re-scan sub-chunks within ANMF payload
                        frame = DecodeVp8Lossy(subData, alphData);
                    }
                }

                if (frame != null)
                {
                    // Convert duration from ms to centiseconds (like GIF)
                    frame.Delay = Math.Max(1, durationMs / 10);
                    frame.DisposeMethod = disposeToBackground ? DisposeType.Background : DisposeType.None;
                    sequence.AddFrame(frame);
                }
            }

            offset += 8 + (int)Math.Min(chunkSize, (uint)(data.Length - offset));
            if ((chunkSize & 1) != 0) offset++;
        }

        if (sequence.Count == 0)
            throw new InvalidDataException("No animation frames found in animated WebP.");

        return sequence;
    }

    /// <summary>
    /// Write an ImageSequence as an animated WebP file.
    /// Uses lossless (VP8L) encoding for each frame.
    /// </summary>
    public static void WriteSequence(ImageSequence sequence, Stream stream, int quality = 100)
    {
        if (sequence.Count == 0)
            throw new ArgumentException("Sequence has no frames.");

        if (sequence.Count == 1)
        {
            Write(sequence[0], stream, quality);
            return;
        }

        // Encode all frames to VP8L/VP8 byte chunks
        var frameChunks = new List<byte[]>();
        bool anyAlpha = false;
        for (int i = 0; i < sequence.Count; i++)
        {
            using var frameMs = new MemoryStream();
            byte[] frameData;
            bool isLossless;
            if (quality >= 100)
            {
                frameData = EncodeLosslessPayload(sequence[i]);
                isLossless = true;
            }
            else
            {
                frameData = EncodeLossyPayload(sequence[i], quality);
                isLossless = false;
            }

            // Build the sub-chunk: "VP8L" + size + data (or "VP8 " for lossy)
            using var subMs = new MemoryStream();
            using (var bw = new BinaryWriter(subMs, System.Text.Encoding.ASCII, true))
            {
                if (isLossless)
                    bw.Write("VP8L"u8);
                else
                    bw.Write("VP8 "u8);
                bw.Write((uint)frameData.Length);
                bw.Write(frameData);
                if ((frameData.Length & 1) != 0) bw.Write((byte)0);
            }
            frameChunks.Add(subMs.ToArray());

            if (sequence[i].HasAlpha) anyAlpha = true;
        }

        int canvasWidth = sequence.CanvasWidth > 0 ? sequence.CanvasWidth : (int)sequence[0].Columns;
        int canvasHeight = sequence.CanvasHeight > 0 ? sequence.CanvasHeight : (int)sequence[0].Rows;

        // Build the RIFF payload
        using var payloadMs = new MemoryStream();
        using (var bw = new BinaryWriter(payloadMs, System.Text.Encoding.ASCII, true))
        {
            // VP8X chunk (extended format)
            bw.Write("VP8X"u8);
            bw.Write((uint)10);
            byte vp8xFlags = 0x02; // animation flag
            if (anyAlpha) vp8xFlags |= 0x10; // alpha flag
            bw.Write(vp8xFlags);
            bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); // reserved
            Write24LE(bw, canvasWidth - 1);
            Write24LE(bw, canvasHeight - 1);

            // ANIM chunk
            bw.Write("ANIM"u8);
            bw.Write((uint)6);
            bw.Write((uint)0); // background color (BGRA, transparent)
            bw.Write((ushort)sequence.LoopCount);

            // ANMF chunks
            for (int i = 0; i < sequence.Count; i++)
            {
                var frame = sequence[i];
                int frameWidth = (int)frame.Columns;
                int frameHeight = (int)frame.Rows;
                int durationMs = frame.Delay * 10; // centiseconds to ms
                if (durationMs < 1) durationMs = 100; // default 100ms

                byte[] subChunk = frameChunks[i];
                int anmfPayloadSize = 16 + subChunk.Length;

                bw.Write("ANMF"u8);
                bw.Write((uint)anmfPayloadSize);
                Write24LE(bw, 0); // frame X / 2
                Write24LE(bw, 0); // frame Y / 2
                Write24LE(bw, frameWidth - 1);
                Write24LE(bw, frameHeight - 1);
                Write24LE(bw, durationMs);
                byte disposeFlag = frame.DisposeMethod == DisposeType.Background ? (byte)0x01 : (byte)0x00;
                bw.Write(disposeFlag);
                bw.Write(subChunk);

                if ((anmfPayloadSize & 1) != 0) bw.Write((byte)0);
            }
        }

        byte[] riffPayload = payloadMs.ToArray();
        int riffSize = 4 + riffPayload.Length; // "WEBP" + payload

        using var finalBw = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        finalBw.Write("RIFF"u8);
        finalBw.Write((uint)riffSize);
        finalBw.Write("WEBP"u8);
        finalBw.Write(riffPayload);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Read24LE(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write24LE(BinaryWriter bw, int value)
    {
        bw.Write((byte)(value & 0xFF));
        bw.Write((byte)((value >> 8) & 0xFF));
        bw.Write((byte)((value >> 16) & 0xFF));
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8L Bit Reader (LSB-first)
    // ═══════════════════════════════════════════════════════════════════

    private struct Vp8lBitReader
    {
        private readonly byte[] data;
        private int bytePos;
        private ulong buffer;
        private int bitsAvailable;

        public Vp8lBitReader(byte[] data, int startOffset)
        {
            this.data = data;
            bytePos = startOffset;
            buffer = 0;
            bitsAvailable = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int n)
        {
            while (bitsAvailable < n)
            {
                if (bytePos < data.Length)
                {
                    buffer |= (ulong)data[bytePos++] << bitsAvailable;
                }

                bitsAvailable += 8;
            }
            uint result = (uint)(buffer & ((1UL << n) - 1));
            buffer >>= n;
            bitsAvailable -= n;
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8L Bit Writer (LSB-first)
    // ═══════════════════════════════════════════════════════════════════

    private struct Vp8lBitWriter
    {
        private byte[] data;
        private int bytePos;
        private ulong buffer;
        private int bitsUsed;

        public Vp8lBitWriter(int initialCapacity)
        {
            data = new byte[Math.Max(initialCapacity, 256)];
            bytePos = 0;
            buffer = 0;
            bitsUsed = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(uint value, int numBits)
        {
            buffer |= (ulong)value << bitsUsed;
            bitsUsed += numBits;
            while (bitsUsed >= 8)
            {
                EnsureCapacity(1);
                data[bytePos++] = (byte)(buffer & 0xFF);
                buffer >>= 8;
                bitsUsed -= 8;
            }
        }

        public byte[] Finish()
        {
            if (bitsUsed > 0)
            {
                EnsureCapacity(1);
                data[bytePos++] = (byte)(buffer & 0xFF);
            }
            var result = new byte[bytePos];
            Buffer.BlockCopy(data, 0, result, 0, bytePos);
            return result;
        }

        private void EnsureCapacity(int extra)
        {
            if (bytePos + extra > data.Length)
            {
                Array.Resize(ref data, data.Length * 2);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Huffman Tree (LSB-first canonical codes)
    // ═══════════════════════════════════════════════════════════════════

    private sealed class HuffmanTree
    {
        private int[]? children; // children[node*2+bit]: >=0 internal, <0 leaf -(sym+1)
        private int nodeCount;
        private int singleSymbol;
        private bool isSingleSymbol;

        public static HuffmanTree Build(int[] codeLengths)
        {
            var tree = new HuffmanTree();
            int nonZero = 0, lastSym = 0;
            for (int i = 0;i < codeLengths.Length;i++)
            {
                if (codeLengths[i] > 0)
                {
                    nonZero++;
                    lastSym = i;
                }
            }

            if (nonZero <= 1)
            {
                tree.isSingleSymbol = true;
                tree.singleSymbol = nonZero == 0 ? 0 : lastSym;
                return tree;
            }

            int maxLen = 0;
            for (int i = 0;i < codeLengths.Length;i++)
            {
                if (codeLengths[i] > maxLen)
                {
                    maxLen = codeLengths[i];
                }
            }

            int[] blCount = new int[maxLen + 1];
            for (int i = 0;i < codeLengths.Length;i++)
            {
                if (codeLengths[i] > 0)
                {
                    blCount[codeLengths[i]]++;
                }
            }

            int[] nextCode = new int[maxLen + 1];
            int code = 0;
            for (int bits = 1;bits <= maxLen;bits++)
            {
                code = (code + blCount[bits - 1]) << 1;
                nextCode[bits] = code;
            }

            int maxNodes = nonZero * 2 + 2;
            tree.children = new int[maxNodes * 2];
            Array.Fill(tree.children, int.MinValue);
            tree.nodeCount = 1;

            for (int sym = 0;sym < codeLengths.Length;sym++)
            {
                int len = codeLengths[sym];
                if (len == 0)
                {
                    continue;
                }

                int hcode = nextCode[len]++;
                int reversed = ReverseBits(hcode, len);

                int node = 0;
                for (int i = 0;i < len;i++)
                {
                    int bit = (reversed >> i) & 1;
                    int idx = node * 2 + bit;
                    if (i == len - 1)
                    {
                        tree.children[idx] = -(sym + 1);
                    }
                    else
                    {
                        if (tree.children[idx] == int.MinValue || tree.children[idx] < 0)
                        {
                            tree.children[idx] = tree.nodeCount++;
                        }

                        node = tree.children[idx];
                    }
                }
            }
            return tree;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadSymbol(ref Vp8lBitReader reader)
        {
            if (isSingleSymbol)
            {
                return singleSymbol;
            }

            int node = 0;
            while (true)
            {
                int bit = (int)reader.ReadBits(1);
                int child = children![node * 2 + bit];
                if (child < 0 && child != int.MinValue)
                {
                    return -(child + 1);
                }

                if (child == int.MinValue)
                {
                    throw new InvalidDataException("Invalid Huffman code in VP8L stream.");
                }

                node = child;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReverseBits(int value, int numBits)
        {
            int result = 0;
            for (int i = 0;i < numBits;i++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8L Decoder
    // ═══════════════════════════════════════════════════════════════════

    private enum Vp8lTransformType
    {
        Predictor = 0,
        Color = 1,
        SubtractGreen = 2,
        ColorIndexing = 3
    }

    private struct Vp8lTransform
    {
        public Vp8lTransformType Type;
        public int SizeBits;
        public uint[]? SubImage;
        public uint[]? ColorTable;
        public int ColorTableSize;
        public int WidthBits;
    }

    private static ImageFrame DecodeVp8l(byte[] data)
    {
        if (data.Length < 1 || data[0] != Vp8lSignatureByte)
        {
            throw new InvalidDataException("Invalid VP8L signature byte.");
        }

        var reader = new Vp8lBitReader(data, 1);
        int width = (int)reader.ReadBits(14) + 1;
        int height = (int)reader.ReadBits(14) + 1;
        bool alphaUsed = reader.ReadBits(1) == 1;
        uint version = reader.ReadBits(3);
        if (version != 0)
        {
            throw new InvalidDataException($"Unsupported VP8L version: {version}");
        }

        if (width > MaxWebpDimension || height > MaxWebpDimension)
        {
            throw new InvalidDataException("WebP image dimensions exceed maximum.");
        }

        int decodedWidth = width;
        var transforms = ReadTransforms(ref reader, ref decodedWidth, height);
        uint[] pixels = DecodeImageStream(ref reader, decodedWidth, height, true);
        ApplyInverseTransforms(pixels, ref decodedWidth, height, transforms, width);

        return ArgbToImageFrame(pixels, width, height, alphaUsed);
    }

    private static List<Vp8lTransform> ReadTransforms(ref Vp8lBitReader reader, ref int width, int height)
    {
        var transforms = new List<Vp8lTransform>();
        while (reader.ReadBits(1) == 1)
        {
            var type = (Vp8lTransformType)reader.ReadBits(2);
            var t = new Vp8lTransform { Type = type };

            switch (type)
            {
                case Vp8lTransformType.Predictor:
                case Vp8lTransformType.Color:
                {
                    int sizeBits = (int)reader.ReadBits(3) + 2;
                    t.SizeBits = sizeBits;
                    int tw = DivRoundUp(width, 1 << sizeBits);
                    int th = DivRoundUp(height, 1 << sizeBits);
                    t.SubImage = DecodeImageStream(ref reader, tw, th, false);
                    break;
                }
                case Vp8lTransformType.SubtractGreen:
                    break;
                case Vp8lTransformType.ColorIndexing:
                {
                    int tableSize = (int)reader.ReadBits(8) + 1;
                    t.ColorTableSize = tableSize;
                    uint[] raw = DecodeImageStream(ref reader, tableSize, 1, false);
                    t.ColorTable = new uint[tableSize];
                    t.ColorTable[0] = raw[0];
                    for (int i = 1;i < tableSize;i++)
                    {
                        t.ColorTable[i] = AddArgb(t.ColorTable[i - 1], raw[i]);
                    }

                    if (tableSize <= 2)
                    {
                        t.WidthBits = 3;
                    }
                    else if (tableSize <= 4)
                    {
                        t.WidthBits = 2;
                    }
                    else if (tableSize <= 16)
                    {
                        t.WidthBits = 1;
                    }
                    else
                    {
                        t.WidthBits = 0;
                    }

                    if (t.WidthBits > 0)
                    {
                        width = DivRoundUp(width, 1 << t.WidthBits);
                    }

                    break;
                }
            }
            transforms.Add(t);
        }
        return transforms;
    }

    private static uint[] DecodeImageStream(ref Vp8lBitReader reader, int width, int height, bool isMainImage)
    {
        int colorCacheBits = 0;
        if (reader.ReadBits(1) == 1)
        {
            colorCacheBits = (int)reader.ReadBits(4);
            if (colorCacheBits < 1 || colorCacheBits > 11)
            {
                throw new InvalidDataException("Invalid color cache bits.");
            }
        }
        int colorCacheSize = colorCacheBits > 0 ? 1 << colorCacheBits : 0;

        int numGroups = 1;
        int[]? metaImage = null;
        int metaBits = 0, metaWidth = 0;

        if (isMainImage && reader.ReadBits(1) == 1)
        {
            metaBits = (int)reader.ReadBits(3) + 2;
            metaWidth = DivRoundUp(width, 1 << metaBits);
            int metaHeight = DivRoundUp(height, 1 << metaBits);
            uint[] entropyImg = DecodeImageStream(ref reader, metaWidth, metaHeight, false);

            int maxCode = 0;
            for (int i = 0;i < entropyImg.Length;i++)
            {
                int mc = (int)((entropyImg[i] >> 8) & 0xFFFF);
                if (mc > maxCode)
                {
                    maxCode = mc;
                }
            }
            numGroups = maxCode + 1;
            metaImage = new int[entropyImg.Length];
            for (int i = 0;i < entropyImg.Length;i++)
            {
                metaImage[i] = (int)((entropyImg[i] >> 8) & 0xFFFF);
            }
        }

        int greenAlpha = 256 + 24 + colorCacheSize;
        var groups = new HuffmanTree[numGroups][];
        for (int g = 0;g < numGroups;g++)
        {
            groups[g] = [ ReadHuffmanTree(ref reader, greenAlpha), ReadHuffmanTree(ref reader, 256), ReadHuffmanTree(ref reader, 256), ReadHuffmanTree(ref reader, 256), ReadHuffmanTree(ref reader, 40), ];
        }

        uint[] pixels = new uint[width * height];
        uint[]? cache = colorCacheSize > 0 ? new uint[colorCacheSize] : null;
        int pos = 0, total = width * height;

        while (pos < total)
        {
            int gi = 0;
            if (metaImage != null)
            {
                int x = pos % width, y = pos / width;
                int mi = (y >> metaBits) * metaWidth + (x >> metaBits);
                if (mi < metaImage.Length)
                {
                    gi = metaImage[mi];
                }

                if (gi >= numGroups)
                {
                    gi = 0;
                }
            }
            var g = groups[gi];
            int s = g[0].ReadSymbol(ref reader);

            if (s < 256)
            {
                int green = s;
                int red = g[1].ReadSymbol(ref reader);
                int blue = g[2].ReadSymbol(ref reader);
                int alpha = g[3].ReadSymbol(ref reader);
                uint px = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                pixels[pos] = px;
                if (cache != null)
                {
                    cache[PixelHash(px, colorCacheBits)] = px;
                }

                pos++;
            }
            else if (s < 256 + 24)
            {
                int length = DecodePrefixValue(s - 256, ref reader);
                int distCode = g[4].ReadSymbol(ref reader);
                int rawDist = DecodePrefixValue(distCode, ref reader);
                int dist = MapDistance(rawDist, width);
                int src = pos - dist;
                for (int i = 0;i < length && pos < total;i++, pos++)
                {
                    int sp = src + i;
                    uint px = (sp >= 0 && sp < total) ? pixels[sp] : 0;
                    pixels[pos] = px;
                    if (cache != null)
                    {
                        cache[PixelHash(px, colorCacheBits)] = px;
                    }
                }
            }
            else
            {
                int cacheIdx = s - 256 - 24;
                uint px = (cache != null && cacheIdx < cache.Length) ? cache[cacheIdx] : 0;
                pixels[pos] = px;
                if (cache != null)
                {
                    cache[PixelHash(px, colorCacheBits)] = px;
                }

                pos++;
            }
        }
        return pixels;
    }

    private static HuffmanTree ReadHuffmanTree(ref Vp8lBitReader reader, int alphabetSize)
    {
        if (reader.ReadBits(1) == 1)
        {
            int numSymbols = (int)reader.ReadBits(1) + 1;
            int[] cl = new int[alphabetSize];
            int isFirst8 = (int)reader.ReadBits(1);
            int sym0 = (int)reader.ReadBits(1 + 7 * isFirst8);
            if (sym0 < alphabetSize)
            {
                cl[sym0] = 1;
            }

            if (numSymbols == 2)
            {
                int sym1 = (int)reader.ReadBits(8);
                if (sym1 < alphabetSize)
                {
                    cl[sym1] = 1;
                }
            }
            return HuffmanTree.Build(cl);
        }

        int numCodeLengths = (int)reader.ReadBits(4) + 4;
        int[] clcl = new int[19];
        for (int i = 0;i < numCodeLengths;i++)
        {
            clcl[CodeLengthCodeOrder[i]] = (int)reader.ReadBits(3);
        }

        var clTree = HuffmanTree.Build(clcl);

        int maxSymbol;
        if (reader.ReadBits(1) == 0)
        {
            maxSymbol = alphabetSize;
        }
        else
        {
            int lengthNBits = (int)(2 + 2 * reader.ReadBits(3));
            maxSymbol = (int)(2 + reader.ReadBits(lengthNBits));
            if (maxSymbol > alphabetSize)
            {
                maxSymbol = alphabetSize;
            }
        }

        int[] codeLengths = new int[alphabetSize];
        int sym = 0;
        int prevNonZero = 8;
        while (sym < maxSymbol)
        {
            int code = clTree.ReadSymbol(ref reader);
            if (code < 16)
            {
                codeLengths[sym++] = code;
                if (code != 0)
                {
                    prevNonZero = code;
                }
            }
            else if (code == 16)
            {
                int reps = (int)(3 + reader.ReadBits(2));
                for (int i = 0;i < reps && sym < maxSymbol;i++)
                {
                    codeLengths[sym++] = prevNonZero;
                }
            }
            else if (code == 17)
            {
                sym += (int)(3 + reader.ReadBits(3));
            }
            else
            {
                sym += (int)(11 + reader.ReadBits(7));
            }
        }
        return HuffmanTree.Build(codeLengths);
    }

    private static void ApplyInverseTransforms(uint[] pixels, ref int width, int height,
        List<Vp8lTransform> transforms, int originalWidth)
    {
        for (int t = transforms.Count - 1;t >= 0;t--)
        {
            var tr = transforms[t];
            switch (tr.Type)
            {
                case Vp8lTransformType.SubtractGreen:
                    InverseSubtractGreen(pixels);
                    break;
                case Vp8lTransformType.Predictor:
                    InversePredictorTransform(pixels, width, height, tr);
                    break;
                case Vp8lTransformType.Color:
                    InverseColorTransform(pixels, width, height, tr);
                    break;
                case Vp8lTransformType.ColorIndexing:
                    pixels = InverseColorIndexing(pixels, ref width, height, tr, originalWidth);
                    break;
            }
        }
    }

    private static void InverseSubtractGreen(uint[] pixels)
    {
        for (int i = 0;i < pixels.Length;i++)
        {
            uint p = pixels[i];
            int g = (int)((p >> 8) & 0xFF);
            int r = (int)(((p >> 16) & 0xFF) + g) & 0xFF;
            int b = (int)((p & 0xFF) + g) & 0xFF;
            pixels[i] = (p & 0xFF00FF00u) | ((uint)r << 16) | (uint)b;
        }
    }

    private static void InversePredictorTransform(uint[] pixels, int width, int height, Vp8lTransform tr)
    {
        int sizeBits = tr.SizeBits;
        int tw = DivRoundUp(width, 1 << sizeBits);

        for (int y = 0;y < height;y++)
        {
            for (int x = 0;x < width;x++)
            {
                int idx = y * width + x;
                uint predicted;
                if (x == 0 && y == 0)
                {
                    predicted = 0xFF000000;
                }
                else if (y == 0)
                {
                    predicted = pixels[idx - 1];
                }
                else if (x == 0)
                {
                    predicted = pixels[idx - width];
                }
                else
                {
                    int bi = (y >> sizeBits) * tw + (x >> sizeBits);
                    int mode = (int)((tr.SubImage![bi] >> 8) & 0xFF);
                    uint l = pixels[idx - 1];
                    uint t = pixels[idx - width];
                    uint tl = pixels[idx - width - 1];
                    uint tr2 = (x < width - 1) ? pixels[idx - width + 1] : pixels[y * width];
                    predicted = PredictPixel(mode, l, t, tl, tr2);
                }
                pixels[idx] = AddArgb(pixels[idx], predicted);
            }
        }
    }

    private static void InverseColorTransform(uint[] pixels, int width, int height, Vp8lTransform tr)
    {
        int sizeBits = tr.SizeBits;
        int tw = DivRoundUp(width, 1 << sizeBits);

        for (int y = 0;y < height;y++)
        {
            for (int x = 0;x < width;x++)
            {
                int bi = (y >> sizeBits) * tw + (x >> sizeBits);
                uint cte = tr.SubImage![bi];
                sbyte greenToRed = (sbyte)(byte)(cte & 0xFF);
                sbyte greenToBlue = (sbyte)(byte)((cte >> 8) & 0xFF);
                sbyte redToBlue = (sbyte)(byte)((cte >> 16) & 0xFF);

                int idx = y * width + x;
                uint p = pixels[idx];
                sbyte green = (sbyte)(byte)((p >> 8) & 0xFF);
                int red = (int)((p >> 16) & 0xFF);
                int blue = (int)(p & 0xFF);

                red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                blue = (blue + ((greenToBlue * green) >> 5) + ((redToBlue * (sbyte)(byte)red) >> 5)) & 0xFF;
                pixels[idx] = (p & 0xFF00FF00u) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static uint[] InverseColorIndexing(uint[] pixels, ref int width, int height,
        Vp8lTransform tr, int originalWidth)
    {
        if (tr.WidthBits > 0)
        {
            int pixelsPerPacked = 1 << tr.WidthBits;
            int bitsPerIndex = 8 >> tr.WidthBits;
            int indexMask = (1 << bitsPerIndex) - 1;
            int newWidth = originalWidth;
            var unpacked = new uint[newWidth * height];

            for (int y = 0;y < height;y++)
            {
                for (int x = 0;x < newWidth;x++)
                {
                    int packedX = x >> tr.WidthBits;
                    int bitShift = (x & (pixelsPerPacked - 1)) * bitsPerIndex;
                    int pi = y * width + packedX;
                    int green = (int)((pi < pixels.Length ? pixels[pi] : 0) >> 8) & 0xFF;
                    int index = (green >> bitShift) & indexMask;
                    unpacked[y * newWidth + x] = (index < tr.ColorTableSize) ? tr.ColorTable![index] : 0;
                }
            }
            width = newWidth;
            return unpacked;
        }
        else
        {
            for (int i = 0;i < pixels.Length;i++)
            {
                int index = (int)((pixels[i] >> 8) & 0xFF);
                pixels[i] = (index < tr.ColorTableSize) ? tr.ColorTable![index] : 0;
            }
            return pixels;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8L Encoder
    // ═══════════════════════════════════════════════════════════════════

    private static void WriteLossless(ImageFrame image, Stream stream)
    {
        int width = (int)image.Columns;
        int height = (int)image.Rows;
        bool hasAlpha = image.HasAlpha;
        int channels = image.NumberOfChannels;

        uint[] argb = new uint[width * height];
        for (int y = 0;y < height;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < width;x++)
            {
                int off = x * channels;
                byte r = Quantum.ScaleToByte(row[off]);
                byte g = Quantum.ScaleToByte(row[off + 1]);
                byte b = Quantum.ScaleToByte(row[off + 2]);
                byte a = hasAlpha ? Quantum.ScaleToByte(row[off + 3]) : (byte)255;
                argb[y * width + x] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            }
        }

        // Apply subtract green transform
        for (int i = 0;i < argb.Length;i++)
        {
            uint p = argb[i];
            int g = (int)((p >> 8) & 0xFF);
            int r = ((int)((p >> 16) & 0xFF) - g) & 0xFF;
            int bl = ((int)(p & 0xFF) - g) & 0xFF;
            argb[i] = (p & 0xFF00FF00u) | ((uint)r << 16) | (uint)bl;
        }

        // Count frequencies (green channel alphabet = 256 literals only, no LZ77/cache)
        long[] greenFreq = new long[256 + 24]; // +24 for length codes (unused but must exist)
        long[] redFreq = new long[256];
        long[] blueFreq = new long[256];
        long[] alphaFreq = new long[256];
        for (int i = 0;i < argb.Length;i++)
        {
            uint p = argb[i];
            greenFreq[(p >> 8) & 0xFF]++;
            redFreq[(p >> 16) & 0xFF]++;
            blueFreq[p & 0xFF]++;
            alphaFreq[p >> 24]++;
        }

        // Ensure at least one symbol in each alphabet
        bool hasGreenSymbol = false;
        for (int i = 0;i < greenFreq.Length;i++)
        {
            if (greenFreq[i] > 0)
            {
                hasGreenSymbol = true;
                break;
            }
        }

        if (!hasGreenSymbol)
        {
            greenFreq[0] = 1;
        }

        int[] greenLens = ComputeCodeLengths(greenFreq, 256 + 24, 15);
        int[] redLens = ComputeCodeLengths(redFreq, 256, 15);
        int[] blueLens = ComputeCodeLengths(blueFreq, 256, 15);
        int[] alphaLens = ComputeCodeLengths(alphaFreq, 256, 15);
        int[] distLens = new int[40];
        distLens[0] = 1; // need at least one symbol

        int[] greenCodes = BuildCanonicalCodes(greenLens);
        int[] redCodes = BuildCanonicalCodes(redLens);
        int[] blueCodes = BuildCanonicalCodes(blueLens);
        int[] alphaCodes = BuildCanonicalCodes(alphaLens);

        var writer = new Vp8lBitWriter(argb.Length * 2 + 4096);

        // VP8L header
        writer.WriteBits(Vp8lSignatureByte, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(hasAlpha ? 1u : 0u, 1);
        writer.WriteBits(0, 3); // version

        // Transform: subtract green
        writer.WriteBits(1, 1); // transform present
        writer.WriteBits(2, 2); // SubtractGreen
        writer.WriteBits(0, 1); // no more transforms

        // Image data header
        writer.WriteBits(0, 1); // no color cache
        writer.WriteBits(0, 1); // no meta prefix codes

        // Write 5 Huffman trees
        WriteHuffmanTree(ref writer, greenLens, 256 + 24);
        WriteHuffmanTree(ref writer, redLens, 256);
        WriteHuffmanTree(ref writer, blueLens, 256);
        WriteHuffmanTree(ref writer, alphaLens, 256);
        WriteHuffmanTree(ref writer, distLens, 40);

        // Determine which trees are single-symbol (decoder reads 0 bits for these)
        bool greenSingle = CountNonZeroLengths(greenLens) <= 1;
        bool redSingle = CountNonZeroLengths(redLens) <= 1;
        bool blueSingle = CountNonZeroLengths(blueLens) <= 1;
        bool alphaSingle = CountNonZeroLengths(alphaLens) <= 1;

        // Write pixel data (all literals — skip single-symbol trees to match decoder)
        for (int i = 0;i < argb.Length;i++)
        {
            uint p = argb[i];
            if (!greenSingle)
            {
                WriteReversedCode(ref writer, greenCodes[(p >> 8) & 0xFF], greenLens[(p >> 8) & 0xFF]);
            }

            if (!redSingle)
            {
                WriteReversedCode(ref writer, redCodes[(p >> 16) & 0xFF], redLens[(p >> 16) & 0xFF]);
            }

            if (!blueSingle)
            {
                WriteReversedCode(ref writer, blueCodes[p & 0xFF], blueLens[p & 0xFF]);
            }

            if (!alphaSingle)
            {
                WriteReversedCode(ref writer, alphaCodes[p >> 24], alphaLens[p >> 24]);
            }
        }

        byte[] vp8lData = writer.Finish();
        WriteRiffContainer(stream, vp8lData, isLossless: true);
    }

    /// <summary>Encode frame as VP8L payload bytes (no RIFF/chunk header).</summary>
    private static byte[] EncodeLosslessPayload(ImageFrame image)
    {
        using var ms = new MemoryStream();
        // Reuse WriteLossless but capture the VP8L data
        // We'll just call WriteLossless into a temp stream and extract the VP8L chunk data
        WriteLossless(image, ms);
        ms.Position = 0;
        byte[] riffData = ms.ToArray();
        // RIFF structure: "RIFF"(4) + size(4) + "WEBP"(4) + "VP8L"(4) + chunkSize(4) + payload
        int payloadStart = 20; // 4+4+4+4+4
        uint chunkSize = BitConverter.ToUInt32(riffData, 16);
        byte[] payload = new byte[chunkSize];
        Buffer.BlockCopy(riffData, payloadStart, payload, 0, (int)chunkSize);
        return payload;
    }

    /// <summary>Encode frame as VP8 lossy payload bytes (no RIFF/chunk header).</summary>
    private static byte[] EncodeLossyPayload(ImageFrame image, int quality)
    {
        using var ms = new MemoryStream();
        WriteLossy(image, ms, quality);
        ms.Position = 0;
        byte[] riffData = ms.ToArray();
        int payloadStart = 20;
        uint chunkSize = BitConverter.ToUInt32(riffData, 16);
        byte[] payload = new byte[chunkSize];
        Buffer.BlockCopy(riffData, payloadStart, payload, 0, (int)chunkSize);
        return payload;
    }

    private static void WriteRiffContainer(Stream stream, byte[] chunkData, bool isLossless)
    {
        byte[] tag = isLossless ? "VP8L"u8.ToArray() : "VP8 "u8.ToArray();
        int chunkLen = chunkData.Length;
        int riffSize = 4 + 8 + chunkLen; // "WEBP" + chunk header + data
        if ((chunkLen & 1) != 0)
        {
            riffSize++;
        }

        using var bw = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        bw.Write("RIFF"u8);
        bw.Write((uint)riffSize);
        bw.Write("WEBP"u8);
        bw.Write(tag);
        bw.Write((uint)chunkLen);
        bw.Write(chunkData);
        if ((chunkLen & 1) != 0)
        {
            bw.Write((byte)0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteReversedCode(ref Vp8lBitWriter writer, int code, int length)
    {
        if (length == 0)
        {
            return;
        }

        int reversed = 0;
        for (int i = 0;i < length;i++)
        {
            reversed = (reversed << 1) | (code & 1);
            code >>= 1;
        }
        writer.WriteBits((uint)reversed, length);
    }

    private static void WriteHuffmanTree(ref Vp8lBitWriter writer, int[] codeLengths, int alphabetSize)
    {
        int nonZero = 0;
        int sym0 = -1, sym1 = -1;
        for (int i = 0;i < alphabetSize;i++)
        {
            if (codeLengths[i] > 0)
            {
                nonZero++;
                if (sym0 < 0)
                {
                    sym0 = i;
                }
                else if (sym1 < 0)
                {
                    sym1 = i;
                }
            }
        }

        // Use simple code if possible
        if (nonZero <= 2 && sym0 >= 0 && sym0 <= 255 && (sym1 < 0 || sym1 <= 255))
        {
            writer.WriteBits(1, 1); // simple
            writer.WriteBits((uint)(nonZero - 1), 1);
            if (sym0 <= 1)
            {
                writer.WriteBits(0, 1);
                writer.WriteBits((uint)sym0, 1);
            }
            else
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)sym0, 8);
            }
            if (nonZero == 2)
            {
                writer.WriteBits((uint)sym1!, 8);
            }

            return;
        }

        writer.WriteBits(0, 1); // normal code

        // Compute meta-Huffman for code length symbols
        long[] clFreq = new long[19];
        for (int i = 0;i < alphabetSize;i++)
        {
            clFreq[codeLengths[i]]++;
        }

        int[] clLens = ComputeCodeLengths(clFreq, 19, 7);
        int[] clCodes = BuildCanonicalCodes(clLens);

        int numClCodes = 4;
        for (int i = 18;i >= 4;i--)
        {
            if (clLens[CodeLengthCodeOrder[i]] > 0)
            {
                numClCodes = i + 1;
                break;
            }
        }

        writer.WriteBits((uint)(numClCodes - 4), 4);
        for (int i = 0;i < numClCodes;i++)
        {
            writer.WriteBits((uint)clLens[CodeLengthCodeOrder[i]], 3);
        }

        writer.WriteBits(0, 1); // max_symbol = default (alphabetSize)

        // Check if the meta-Huffman is single-symbol (decoder reads 0 bits per symbol)
        bool metaSingle = CountNonZeroLengths(clLens) <= 1;

        for (int i = 0;i < alphabetSize;i++)
        {
            if (!metaSingle)
            {
                WriteReversedCode(ref writer, clCodes[codeLengths[i]], clLens[codeLengths[i]]);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VP8 Lossy Decoder / Encoder
    // ═══════════════════════════════════════════════════════════════════

    private static ImageFrame DecodeVp8Lossy(byte[] data, byte[]? alphData)
    {
        return Vp8LossyCodec.Decode(data, alphData);
    }

    private static void WriteLossy(ImageFrame image, Stream stream, int quality)
    {
        byte[] vp8Data = Vp8LossyCodec.Encode(image, quality);
        WriteRiffContainer(stream, vp8Data, isLossless: false);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Huffman Code Length Builder
    // ═══════════════════════════════════════════════════════════════════

    private static int[] ComputeCodeLengths(long[] frequencies, int alphabetSize, int maxLength)
    {
        int[] lengths = new int[alphabetSize];
        int nonZero = 0;
        int lastSym = 0;
        for (int i = 0;i < Math.Min(frequencies.Length, alphabetSize);i++)
        {
            if (frequencies[i] > 0)
            {
                nonZero++;
                lastSym = i;
            }
        }

        if (nonZero == 0)
        {
            lengths[0] = 1;
            return lengths;
        }
        if (nonZero == 1)
        {
            lengths[lastSym] = 1;
            return lengths;
        }

        // Build Huffman tree via sorted queue
        int totalNodes = nonZero * 2;
        long[] nodeFreq = new long[totalNodes];
        int[] nodeLeft = new int[totalNodes];
        int[] nodeRight = new int[totalNodes];
        int[] nodeSym = new int[totalNodes];
        Array.Fill(nodeLeft, -1);
        Array.Fill(nodeRight, -1);
        Array.Fill(nodeSym, -1);
        int nCount = 0;

        for (int i = 0;i < Math.Min(frequencies.Length, alphabetSize);i++)
        {
            if (frequencies[i] > 0)
            {
                nodeFreq[nCount] = frequencies[i];
                nodeSym[nCount] = i;
                nCount++;
            }
        }

        var queue = new List<int>(nCount);
        for (int i = 0;i < nCount;i++)
        {
            queue.Add(i);
        }

        queue.Sort((a, b) => nodeFreq[a].CompareTo(nodeFreq[b]));

        while (queue.Count > 1)
        {
            int a = queue[0];
            queue.RemoveAt(0);
            int b = queue[0];
            queue.RemoveAt(0);
            int parent = nCount++;
            nodeFreq[parent] = nodeFreq[a] + nodeFreq[b];
            nodeLeft[parent] = a;
            nodeRight[parent] = b;

            int ins = 0;
            while (ins < queue.Count && nodeFreq[queue[ins]] <= nodeFreq[parent])
            {
                ins++;
            }

            queue.Insert(ins, parent);
        }

        void ComputeDepth(int node, int depth)
        {
            if (nodeLeft[node] == -1)
            {
                lengths[nodeSym[node]] = depth;
                return;
            }
            ComputeDepth(nodeLeft[node], depth + 1);
            ComputeDepth(nodeRight[node], depth + 1);
        }

        ComputeDepth(queue[0], 0);

        // Limit code lengths
        bool needsLimit = false;
        for (int i = 0;i < alphabetSize;i++)
        {
            if (lengths[i] > maxLength)
            {
                needsLimit = true;
                break;
            }
        }

        if (needsLimit)
        {
            for (int i = 0;i < alphabetSize;i++)
            {
                if (lengths[i] > maxLength)
                {
                    lengths[i] = maxLength;
                }
            }

            // Adjust for Kraft inequality
            while (true)
            {
                long kraft = 0;
                for (int i = 0;i < alphabetSize;i++)
                {
                    if (lengths[i] > 0)
                    {
                        kraft += 1L << (maxLength - lengths[i]);
                    }
                }

                long target = 1L << maxLength;
                if (kraft <= target)
                {
                    break;
                }

                for (int i = 0;i < alphabetSize && kraft > target;i++)
                {
                    while (lengths[i] > 0 && lengths[i] < maxLength && kraft > target)
                    {
                        kraft -= 1L << (maxLength - lengths[i]);
                        lengths[i]++;
                        kraft += 1L << (maxLength - lengths[i]);
                    }
                }
                break;
            }
        }

        return lengths;
    }

    private static int[] BuildCanonicalCodes(int[] lengths)
    {
        int maxLen = 0;
        for (int i = 0;i < lengths.Length;i++)
        {
            if (lengths[i] > maxLen)
            {
                maxLen = lengths[i];
            }
        }

        if (maxLen == 0)
        {
            return new int[lengths.Length];
        }

        int[] blCount = new int[maxLen + 1];
        for (int i = 0;i < lengths.Length;i++)
        {
            if (lengths[i] > 0)
            {
                blCount[lengths[i]]++;
            }
        }

        int[] nextCode = new int[maxLen + 1];
        int code = 0;
        for (int bits = 1;bits <= maxLen;bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        int[] codes = new int[lengths.Length];
        for (int i = 0;i < lengths.Length;i++)
        {
            if (lengths[i] > 0)
            {
                codes[i] = nextCode[lengths[i]]++;
            }
        }

        return codes;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Utility Methods
    // ═══════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddArgb(uint a, uint b)
    {
        uint a0 = ((a >> 24) + (b >> 24)) & 0xFF;
        uint a1 = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) & 0xFF;
        uint a2 = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) & 0xFF;
        uint a3 = ((a & 0xFF) + (b & 0xFF)) & 0xFF;
        return (a0 << 24) | (a1 << 16) | (a2 << 8) | a3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PixelHash(uint argb, int bits)
    {
        return (ColorCacheHashMul * argb) >> (32 - bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodePrefixValue(int prefixCode, ref Vp8lBitReader reader)
    {
        if (prefixCode < 4)
        {
            return prefixCode + 1;
        }

        int extraBits = (prefixCode - 2) >> 1;
        int offset = (2 + (prefixCode & 1)) << extraBits;
        return offset + (int)reader.ReadBits(extraBits) + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MapDistance(int rawDist, int imageWidth)
    {
        if (rawDist > 120)
        {
            return rawDist - 120;
        }

        int dx = DistMapDx[rawDist - 1];
        int dy = DistMapDy[rawDist - 1];
        int d = dx + dy * imageWidth;
        return d < 1 ? 1 : d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DivRoundUp(int num, int den) => (num + den - 1) / den;

    private static int CountNonZeroLengths(int[] lengths)
    {
        int count = 0;
        for (int i = 0;i < lengths.Length;i++)
        {
            if (lengths[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static uint PredictPixel(int mode, uint l, uint t, uint tl, uint tr)
    {
        return mode switch
        {
            0 => 0xFF000000,
            1 => l,
            2 => t,
            3 => tr,
            4 => tl,
            5 => Avg2(Avg2(l, tr), t),
            6 => Avg2(l, tl),
            7 => Avg2(l, t),
            8 => Avg2(tl, t),
            9 => Avg2(t, tr),
            10 => Avg2(Avg2(l, tl), Avg2(t, tr)),
            11 => SelectPredictor(l, t, tl),
            12 => ClampAddSubFull(l, t, tl),
            13 => ClampAddSubHalf(Avg2(l, t), tl),
            _ => 0xFF000000,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Avg2(uint a, uint b)
    {
        return (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);
    }

    private static uint SelectPredictor(uint l, uint t, uint tl)
    {
        int pA = C(l, 24) + C(t, 24) - C(tl, 24);
        int pR = C(l, 16) + C(t, 16) - C(tl, 16);
        int pG = C(l, 8) + C(t, 8) - C(tl, 8);
        int pB = C(l, 0) + C(t, 0) - C(tl, 0);
        int distL = Math.Abs(pA - C(l, 24)) + Math.Abs(pR - C(l, 16)) +
                    Math.Abs(pG - C(l, 8)) + Math.Abs(pB - C(l, 0));
        int distT = Math.Abs(pA - C(t, 24)) + Math.Abs(pR - C(t, 16)) +
                    Math.Abs(pG - C(t, 8)) + Math.Abs(pB - C(t, 0));
        return distL < distT ? l : t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int C(uint pixel, int shift) => (int)((pixel >> shift) & 0xFF);

    private static uint ClampAddSubFull(uint a, uint b, uint c)
    {
        return Pack(
            Clamp255(C(a, 24) + C(b, 24) - C(c, 24)),
            Clamp255(C(a, 16) + C(b, 16) - C(c, 16)),
            Clamp255(C(a, 8) + C(b, 8) - C(c, 8)),
            Clamp255(C(a, 0) + C(b, 0) - C(c, 0)));
    }

    private static uint ClampAddSubHalf(uint a, uint b)
    {
        return Pack(
            Clamp255(C(a, 24) + (C(a, 24) - C(b, 24)) / 2),
            Clamp255(C(a, 16) + (C(a, 16) - C(b, 16)) / 2),
            Clamp255(C(a, 8) + (C(a, 8) - C(b, 8)) / 2),
            Clamp255(C(a, 0) + (C(a, 0) - C(b, 0)) / 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pack(int a, int r, int g, int b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static ImageFrame ArgbToImageFrame(uint[] argb, int width, int height, bool hasAlpha)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        frame.Compression = CompressionType.WebP;
        int channels = frame.NumberOfChannels;

        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                uint p = argb[y * width + x];
                int off = x * channels;
                row[off] = Quantum.ScaleFromByte((byte)((p >> 16) & 0xFF));     // R
                row[off + 1] = Quantum.ScaleFromByte((byte)((p >> 8) & 0xFF));  // G
                row[off + 2] = Quantum.ScaleFromByte((byte)(p & 0xFF));         // B
                if (hasAlpha)
                {
                    row[off + 3] = Quantum.ScaleFromByte((byte)(p >> 24));       // A
                }
            }
        }
        return frame;
    }
}
