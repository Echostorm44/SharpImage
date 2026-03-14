// DDS (DirectDraw Surface) format coder — read and write.
// Supports uncompressed BGRA32, DXT1/BC1, DXT3, DXT5/BC3, and BC7 compressed textures.
// BC7 uses DX10 extended header with DXGI_FORMAT_BC7_UNORM_SRGB.
// Reads only the first mipmap level (base image).

using SharpImage.Core;
using SharpImage.Image;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats;

public static class DdsCoder
{
    private const uint DdsMagic = 0x20534444; // "DDS "
    private const int HeaderSize = 128; // magic(4) + DDSURFACEDESC2(124)

    // Pixel format flags
    private const uint DDPF_ALPHAPIXELS = 0x1;
    private const uint DDPF_FOURCC = 0x4;
    private const uint DDPF_RGB = 0x40;

    // FourCC codes
    private const uint FOURCC_DXT1 = 0x31545844;
    private const uint FOURCC_DXT3 = 0x33545844;
    private const uint FOURCC_DXT5 = 0x35545844;
    private const uint FOURCC_DX10 = 0x30315844;

    // DXGI formats for DX10 extended header
    private const uint DXGI_FORMAT_BC7_UNORM = 98;
    private const uint DXGI_FORMAT_BC7_UNORM_SRGB = 99;

    /// <summary>
    /// Detect DDS format by magic number.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return magic == DdsMagic;
    }

    /// <summary>
    /// Decode a DDS texture (first mipmap level).
    /// </summary>
    public static ImageFrame Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new InvalidDataException("Not a valid DDS file");
        }

        int pos = 4; // skip magic
        uint descSize = ReadU32LE(data, ref pos); // should be 124
        uint flags = ReadU32LE(data, ref pos);
        int height = (int)ReadU32LE(data, ref pos);
        int width = (int)ReadU32LE(data, ref pos);
        uint pitchOrLinearSize = ReadU32LE(data, ref pos);
        uint depth = ReadU32LE(data, ref pos);
        uint mipmapCount = ReadU32LE(data, ref pos);
        pos += 44; // skip 11 reserved DWORDs

        // Read DDPIXELFORMAT (32 bytes)
        uint pfSize = ReadU32LE(data, ref pos);
        uint pfFlags = ReadU32LE(data, ref pos);
        uint fourCC = ReadU32LE(data, ref pos);
        uint rgbBitCount = ReadU32LE(data, ref pos);
        uint rMask = ReadU32LE(data, ref pos);
        uint gMask = ReadU32LE(data, ref pos);
        uint bMask = ReadU32LE(data, ref pos);
        uint aMask = ReadU32LE(data, ref pos);

        // Skip caps (20 bytes: caps + caps2 + 3 reserved DWORDs)
        pos += 20;

        var pixelData = data[HeaderSize..];

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            if (fourCC == FOURCC_DX10)
            {
                // DX10 extended header (20 bytes after standard header)
                int dx10Pos = HeaderSize;
                uint dxgiFormat = ReadU32LE(data, ref dx10Pos);
                ReadU32LE(data, ref dx10Pos); // resource dimension
                ReadU32LE(data, ref dx10Pos); // misc flag
                ReadU32LE(data, ref dx10Pos); // array size
                ReadU32LE(data, ref dx10Pos); // misc flags 2
                var dx10Data = data[(HeaderSize + 20)..];

                return dxgiFormat switch
                {
                    DXGI_FORMAT_BC7_UNORM or DXGI_FORMAT_BC7_UNORM_SRGB => DecodeBc7(dx10Data, width, height),
                    _ => throw new InvalidDataException($"Unsupported DXGI format: {dxgiFormat}")
                };
            }

            return fourCC switch
            {
                FOURCC_DXT1 => DecodeDxt1(pixelData, width, height),
                FOURCC_DXT5 => DecodeDxt5(pixelData, width, height),
                FOURCC_DXT3 => DecodeDxt5(pixelData, width, height),
                _ => throw new InvalidDataException($"Unsupported DDS FourCC: 0x{fourCC:X8}")
            };
        }

        if ((pfFlags & DDPF_RGB) != 0)
        {
            bool hasAlpha = (pfFlags & DDPF_ALPHAPIXELS) != 0;
            return DecodeUncompressed(pixelData, width, height, (int)rgbBitCount,
                rMask, gMask, bMask, aMask, hasAlpha);
        }

        throw new InvalidDataException("Unsupported DDS pixel format");
    }

    /// <summary>
    /// Encode an ImageFrame as an uncompressed BGRA32 DDS file.
    /// </summary>
    public static byte[] Encode(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        int pitch = w * 4;
        int dataSize = pitch * h;
        byte[] output = new byte[HeaderSize + dataSize];
        int pos = 0;

        // Magic
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), DdsMagic);
        pos += 4;
        // DDSURFACEDESC2
        WriteU32LE(output, ref pos, 124); // size
        WriteU32LE(output, ref pos, 0x1 | 0x2 | 0x4 | 0x1000 | 0x8); // CAPS|HEIGHT|WIDTH|PIXELFORMAT|PITCH
        WriteU32LE(output, ref pos, (uint)h);
        WriteU32LE(output, ref pos, (uint)w);
        WriteU32LE(output, ref pos, (uint)pitch);
        WriteU32LE(output, ref pos, 0); // depth
        WriteU32LE(output, ref pos, 0); // mipmapCount
        // 11 reserved DWORDs
        for (int i = 0;i < 11;i++)
        {
            WriteU32LE(output, ref pos, 0);
        }

        // DDPIXELFORMAT
        WriteU32LE(output, ref pos, 32); // size
        WriteU32LE(output, ref pos, DDPF_RGB | DDPF_ALPHAPIXELS); // flags
        WriteU32LE(output, ref pos, 0); // fourCC
        WriteU32LE(output, ref pos, 32); // rgbBitCount
        WriteU32LE(output, ref pos, 0x00FF0000); // rMask
        WriteU32LE(output, ref pos, 0x0000FF00); // gMask
        WriteU32LE(output, ref pos, 0x000000FF); // bMask
        WriteU32LE(output, ref pos, 0xFF000000); // aMask

        // Caps
        WriteU32LE(output, ref pos, 0x1000); // DDSCAPS_TEXTURE
        WriteU32LE(output, ref pos, 0);
        WriteU32LE(output, ref pos, 0);
        WriteU32LE(output, ref pos, 0);
        WriteU32LE(output, ref pos, 0); // dwReserved2[2]

        // Pixel data (BGRA)
        for (int y = 0;y < h;y++)
        {
            var row = image.GetPixelRow(y);
            for (int x = 0;x < w;x++)
            {
                int srcIdx = x * channels;
                byte r = Quantum.ScaleToByte(row[srcIdx]);
                byte g = channels > 1 ? Quantum.ScaleToByte(row[srcIdx + 1]) : r;
                byte b = channels > 2 ? Quantum.ScaleToByte(row[srcIdx + 2]) : r;
                byte a = hasAlpha ? Quantum.ScaleToByte(row[srcIdx + channels - 1]) : (byte)255;

                output[pos++] = b;
                output[pos++] = g;
                output[pos++] = r;
                output[pos++] = a;
            }
        }

        return output;
    }

    #region Uncompressed decoding

    private static ImageFrame DecodeUncompressed(ReadOnlySpan<byte> data,
        int width, int height, int bpp,
        uint rMask, uint gMask, uint bMask, uint aMask, bool hasAlpha)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, hasAlpha);
        int channels = frame.NumberOfChannels;
        int bytesPerPixel = bpp / 8;

        int rShift = CountTrailingZeros(rMask);
        int gShift = CountTrailingZeros(gMask);
        int bShift = CountTrailingZeros(bMask);
        int aShift = hasAlpha ? CountTrailingZeros(aMask) : 0;
        int rBits = CountBits(rMask);
        int gBits = CountBits(gMask);
        int bBits = CountBits(bMask);
        int aBits = hasAlpha ? CountBits(aMask) : 0;

        int pos = 0;
        for (int y = 0;y < height;y++)
        {
            var row = frame.GetPixelRowForWrite(y);
            for (int x = 0;x < width;x++)
            {
                uint pixel = 0;
                for (int b = 0;b < bytesPerPixel && pos < data.Length;b++)
                {
                    pixel |= (uint)data[pos++] << (b * 8);
                }

                byte r = ExpandBits((int)((pixel & rMask) >> rShift), rBits);
                byte g = ExpandBits((int)((pixel & gMask) >> gShift), gBits);
                byte blue = ExpandBits((int)((pixel & bMask) >> bShift), bBits);
                byte a = hasAlpha && aBits > 0
                    ? ExpandBits((int)((pixel & aMask) >> aShift), aBits)
                    : (byte)255;

                int offset = x * channels;
                row[offset] = Quantum.ScaleFromByte(r);
                if (channels > 1)
                {
                    row[offset + 1] = Quantum.ScaleFromByte(g);
                }

                if (channels > 2)
                {
                    row[offset + 2] = Quantum.ScaleFromByte(blue);
                }

                if (hasAlpha)
                {
                    row[offset + channels - 1] = Quantum.ScaleFromByte(a);
                }
            }
        }

        return frame;
    }

    #endregion

    #region DXT1/BC1 decoding

    private static ImageFrame DecodeDxt1(ReadOnlySpan<byte> data, int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = frame.NumberOfChannels;

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = 0;

        Span<byte> colors = stackalloc byte[16]; // 4 colors × RGBA

        for (int by = 0;by < blocksY;by++)
        {
            for (int bx = 0;bx < blocksX;bx++)
            {
                if (pos + 8 > data.Length)
                {
                    break;
                }

                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
                pos += 8;

                DecodeRgb565(c0, out byte r0, out byte g0, out byte b0);
                DecodeRgb565(c1, out byte r1, out byte g1, out byte b1);

                colors[0] = r0;
                colors[1] = g0;
                colors[2] = b0;
                colors[3] = 255;
                colors[4] = r1;
                colors[5] = g1;
                colors[6] = b1;
                colors[7] = 255;

                if (c0 > c1)
                {
                    colors[8] = (byte)((2 * r0 + r1) / 3);
                    colors[9] = (byte)((2 * g0 + g1) / 3);
                    colors[10] = (byte)((2 * b0 + b1) / 3);
                    colors[11] = 255;
                    colors[12] = (byte)((r0 + 2 * r1) / 3);
                    colors[13] = (byte)((g0 + 2 * g1) / 3);
                    colors[14] = (byte)((b0 + 2 * b1) / 3);
                    colors[15] = 255;
                }
                else
                {
                    colors[8] = (byte)((r0 + r1) / 2);
                    colors[9] = (byte)((g0 + g1) / 2);
                    colors[10] = (byte)((b0 + b1) / 2);
                    colors[11] = 255;
                    colors[12] = 0;
                    colors[13] = 0;
                    colors[14] = 0;
                    colors[15] = 0; // transparent
                }

                WriteBlockToFrame(frame, bx * 4, by * 4, width, height, colors, indices, channels);
            }
        }

        return frame;
    }

    #endregion

    #region DXT5/BC3 decoding

    private static ImageFrame DecodeDxt5(ReadOnlySpan<byte> data, int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = frame.NumberOfChannels;

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = 0;

        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        Span<byte> alphaIndices = stackalloc byte[16];

        for (int by = 0;by < blocksY;by++)
        {
            for (int bx = 0;bx < blocksX;bx++)
            {
                if (pos + 16 > data.Length)
                {
                    break;
                }

                // Alpha block
                byte a0 = data[pos];
                byte a1 = data[pos + 1];
                ulong alphaBits = 0;
                for (int i = 2;i < 8;i++)
                {
                    alphaBits |= (ulong)data[pos + i] << ((i - 2) * 8);
                }

                pos += 8;

                // Interpolate alpha values
                alphas[0] = a0;
                alphas[1] = a1;
                if (a0 > a1)
                {
                    for (int i = 2;i < 8;i++)
                    {
                        alphas[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7);
                    }
                }
                else
                {
                    for (int i = 2;i < 6;i++)
                    {
                        alphas[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5);
                    }

                    alphas[6] = 0;
                    alphas[7] = 255;
                }

                for (int i = 0;i < 16;i++)
                {
                    alphaIndices[i] = (byte)((alphaBits >> (i * 3)) & 7);
                }

                // Color block (same as DXT1)
                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
                pos += 8;

                DecodeRgb565(c0, out byte r0, out byte g0, out byte b0);
                DecodeRgb565(c1, out byte r1, out byte g1, out byte b1);

                colors[0] = r0;
                colors[1] = g0;
                colors[2] = b0;
                colors[3] = 255;
                colors[4] = r1;
                colors[5] = g1;
                colors[6] = b1;
                colors[7] = 255;
                colors[8] = (byte)((2 * r0 + r1) / 3);
                colors[9] = (byte)((2 * g0 + g1) / 3);
                colors[10] = (byte)((2 * b0 + b1) / 3);
                colors[11] = 255;
                colors[12] = (byte)((r0 + 2 * r1) / 3);
                colors[13] = (byte)((g0 + 2 * g1) / 3);
                colors[14] = (byte)((b0 + 2 * b1) / 3);
                colors[15] = 255;

                // Write block with alpha from DXT5 block
                for (int py = 0;py < 4;py++)
                {
                    int iy = by * 4 + py;
                    if (iy >= height)
                    {
                        continue;
                    }

                    var row = frame.GetPixelRowForWrite(iy);

                    for (int px = 0;px < 4;px++)
                    {
                        int ix = bx * 4 + px;
                        if (ix >= width)
                        {
                            continue;
                        }

                        int pixelIdx = py * 4 + px;
                        int colorIdx = (int)((indices >> (pixelIdx * 2)) & 3);
                        int ci = colorIdx * 4;

                        int offset = ix * channels;
                        row[offset] = Quantum.ScaleFromByte(colors[ci]);
                        if (channels > 1)
                        {
                            row[offset + 1] = Quantum.ScaleFromByte(colors[ci + 1]);
                        }

                        if (channels > 2)
                        {
                            row[offset + 2] = Quantum.ScaleFromByte(colors[ci + 2]);
                        }

                        row[offset + channels - 1] = Quantum.ScaleFromByte(alphas[alphaIndices[pixelIdx]]);
                    }
                }
            }
        }

        return frame;
    }

    #endregion

    #region BC7 Decoding

    // BC7 mode parameters: [numSubsets, partBits, rotBits, idxSelBits, colorBits, alphaBits, pbitType, indexBits, index2Bits]
    // pbitType: 0=none, 1=unique per endpoint, 2=shared per subset
    private static readonly int[,] Bc7ModeInfo =
    {
        { 3, 4, 0, 0, 4, 0, 1, 3, 0 }, // mode 0
        { 2, 6, 0, 0, 6, 0, 2, 3, 0 }, // mode 1
        { 3, 6, 0, 0, 5, 0, 0, 2, 0 }, // mode 2
        { 2, 6, 0, 0, 7, 0, 1, 2, 0 }, // mode 3
        { 1, 0, 2, 1, 5, 6, 0, 2, 3 }, // mode 4
        { 1, 0, 2, 0, 7, 8, 0, 2, 2 }, // mode 5
        { 1, 0, 0, 0, 7, 7, 1, 4, 0 }, // mode 6
        { 2, 6, 0, 0, 5, 5, 1, 2, 0 }, // mode 7
    };

    // BC7 interpolation weights for 2, 3, and 4-bit indices
    private static readonly byte[][] Bc7Weights =
    [
        [], // 0-bit (unused)
        [], // 1-bit (unused)
        [0, 21, 43, 64], // 2-bit
        [0, 9, 18, 27, 37, 46, 55, 64], // 3-bit
        [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64], // 4-bit
    ];

    // 2-subset partition table: bit i = subset of texel i (0 or 1)
    private static readonly ushort[] Bc7Partition2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
        0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
        0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
        0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
        0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    // 3-subset partition table: 2 bits per texel, bits [2i..2i+1] = subset of texel i
    private static readonly uint[] Bc7Partition3 =
    [
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8,
        0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090,
        0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0,
        0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400,
        0xA08585A0, 0xAAAA9090, 0x00909090, 0x00000000,
        0x69004040, 0x54A84040, 0x69004040, 0x40A8A800,
        0x00009090, 0xA8A8A800, 0x00006904, 0x40006904,
        0x6A690400, 0x04006A69, 0x55009090, 0x04004040,
        0xA8009000, 0x55A80000, 0x00006940, 0x04009040,
        0x54A80440, 0x04006904, 0x00140040, 0x40000014,
        0x00001400, 0x40000014, 0x00001400, 0x54545454,
        0x04000014, 0x50005000, 0x00005000, 0x00005000,
        0x40400014, 0x00005000, 0x40005000, 0x00000050,
    ];

    private static ImageFrame DecodeBc7(ReadOnlySpan<byte> data, int width, int height)
    {
        var frame = new ImageFrame();
        frame.Initialize(width, height, ColorspaceType.SRGB, true);
        int channels = frame.NumberOfChannels;

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int pos = 0;
        Span<byte> rgba = stackalloc byte[64]; // 16 texels × RGBA

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                if (pos + 16 > data.Length) break;
                DecodeBc7Block(data.Slice(pos, 16), rgba);
                pos += 16;

                for (int py = 0; py < 4; py++)
                {
                    int iy = by * 4 + py;
                    if (iy >= height) continue;
                    var row = frame.GetPixelRowForWrite(iy);
                    for (int px = 0; px < 4; px++)
                    {
                        int ix = bx * 4 + px;
                        if (ix >= width) continue;
                        int si = (py * 4 + px) * 4;
                        int di = ix * channels;
                        row[di] = Quantum.ScaleFromByte(rgba[si]);
                        if (channels > 1) row[di + 1] = Quantum.ScaleFromByte(rgba[si + 1]);
                        if (channels > 2) row[di + 2] = Quantum.ScaleFromByte(rgba[si + 2]);
                        row[di + channels - 1] = Quantum.ScaleFromByte(rgba[si + 3]);
                    }
                }
            }
        }

        return frame;
    }

    private static void DecodeBc7Block(ReadOnlySpan<byte> block, Span<byte> outRgba)
    {
        // Determine mode from leading bits
        int mode = -1;
        for (int i = 0; i < 8; i++)
        {
            if ((block[0] & (1 << i)) != 0) { mode = i; break; }
        }
        if (mode < 0) { outRgba.Clear(); return; }

        int bitPos = mode + 1;
        int numSubsets = Bc7ModeInfo[mode, 0];
        int partBits = Bc7ModeInfo[mode, 1];
        int rotBits = Bc7ModeInfo[mode, 2];
        int idxSelBits = Bc7ModeInfo[mode, 3];
        int colorPrec = Bc7ModeInfo[mode, 4];
        int alphaPrec = Bc7ModeInfo[mode, 5];
        int pbitType = Bc7ModeInfo[mode, 6];
        int idxBits = Bc7ModeInfo[mode, 7];
        int idx2Bits = Bc7ModeInfo[mode, 8];

        int partition = ReadBc7Bits(block, ref bitPos, partBits);
        int rotation = ReadBc7Bits(block, ref bitPos, rotBits);
        int idxSel = ReadBc7Bits(block, ref bitPos, idxSelBits);

        int numEp = numSubsets * 2;
        Span<int> epR = stackalloc int[numEp];
        Span<int> epG = stackalloc int[numEp];
        Span<int> epB = stackalloc int[numEp];
        Span<int> epA = stackalloc int[numEp];

        // Read color endpoints (interleaved: all R, then all G, then all B)
        for (int i = 0; i < numEp; i++) epR[i] = ReadBc7Bits(block, ref bitPos, colorPrec);
        for (int i = 0; i < numEp; i++) epG[i] = ReadBc7Bits(block, ref bitPos, colorPrec);
        for (int i = 0; i < numEp; i++) epB[i] = ReadBc7Bits(block, ref bitPos, colorPrec);
        if (alphaPrec > 0)
            for (int i = 0; i < numEp; i++) epA[i] = ReadBc7Bits(block, ref bitPos, alphaPrec);

        // Read and apply p-bits, then unquantize to 8-bit
        if (pbitType == 1)
        {
            for (int i = 0; i < numEp; i++)
            {
                int pbit = ReadBc7Bits(block, ref bitPos, 1);
                epR[i] = Unquantize((epR[i] << 1) | pbit, colorPrec + 1);
                epG[i] = Unquantize((epG[i] << 1) | pbit, colorPrec + 1);
                epB[i] = Unquantize((epB[i] << 1) | pbit, colorPrec + 1);
                epA[i] = alphaPrec > 0 ? Unquantize((epA[i] << 1) | pbit, alphaPrec + 1) : 255;
            }
        }
        else if (pbitType == 2)
        {
            for (int s = 0; s < numSubsets; s++)
            {
                int pbit = ReadBc7Bits(block, ref bitPos, 1);
                for (int e = 0; e < 2; e++)
                {
                    int idx = s * 2 + e;
                    epR[idx] = Unquantize((epR[idx] << 1) | pbit, colorPrec + 1);
                    epG[idx] = Unquantize((epG[idx] << 1) | pbit, colorPrec + 1);
                    epB[idx] = Unquantize((epB[idx] << 1) | pbit, colorPrec + 1);
                    epA[idx] = alphaPrec > 0 ? Unquantize((epA[idx] << 1) | pbit, alphaPrec + 1) : 255;
                }
            }
        }
        else
        {
            for (int i = 0; i < numEp; i++)
            {
                epR[i] = Unquantize(epR[i], colorPrec);
                epG[i] = Unquantize(epG[i], colorPrec);
                epB[i] = Unquantize(epB[i], colorPrec);
                epA[i] = alphaPrec > 0 ? Unquantize(epA[i], alphaPrec) : 255;
            }
        }

        // Find anchor indices (first texel in each subset — its MSB is implicit 0)
        Span<int> anchors = stackalloc int[3];
        anchors[0] = 0;
        anchors[1] = numSubsets >= 2 ? FindAnchor(numSubsets, partition, 1) : -1;
        anchors[2] = numSubsets >= 3 ? FindAnchor(numSubsets, partition, 2) : -1;

        // Read primary indices
        Span<byte> indices1 = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            bool isAnchor = (i == anchors[0]) || (i == anchors[1]) || (i == anchors[2]);
            indices1[i] = (byte)ReadBc7Bits(block, ref bitPos, isAnchor ? idxBits - 1 : idxBits);
        }

        // Read secondary indices (for dual-index modes 4 and 5)
        Span<byte> indices2 = stackalloc byte[16];
        if (idx2Bits > 0)
        {
            for (int i = 0; i < 16; i++)
            {
                bool isAnchor = (i == 0); // secondary anchor is always texel 0
                indices2[i] = (byte)ReadBc7Bits(block, ref bitPos, isAnchor ? idx2Bits - 1 : idx2Bits);
            }
        }

        // Determine which index set to use for color vs alpha
        int colorIdxBits = idxSel == 0 ? idxBits : idx2Bits;
        int alphaIdxBits = idxSel == 0 ? (idx2Bits > 0 ? idx2Bits : idxBits) : idxBits;

        // Interpolate and output
        for (int i = 0; i < 16; i++)
        {
            int subset = GetBc7Subset(numSubsets, partition, i);
            int ep0 = subset * 2;
            int ep1 = subset * 2 + 1;

            byte ci = idxSel == 0 ? indices1[i] : indices2[i];
            byte ai = idxSel == 0 ? (idx2Bits > 0 ? indices2[i] : indices1[i]) : indices1[i];

            int cw = Bc7Weights[colorIdxBits][ci];
            int aw = Bc7Weights[alphaIdxBits][ai];

            byte r = Bc7Interpolate(epR[ep0], epR[ep1], cw);
            byte g = Bc7Interpolate(epG[ep0], epG[ep1], cw);
            byte b = Bc7Interpolate(epB[ep0], epB[ep1], cw);
            byte a = Bc7Interpolate(epA[ep0], epA[ep1], aw);

            // Apply channel rotation
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (b, a) = (a, b); break;
            }

            outRgba[i * 4] = r;
            outRgba[i * 4 + 1] = g;
            outRgba[i * 4 + 2] = b;
            outRgba[i * 4 + 3] = a;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBc7Bits(ReadOnlySpan<byte> block, ref int bitPos, int count)
    {
        if (count == 0) return 0;
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            result |= ((block[bitPos >> 3] >> (bitPos & 7)) & 1) << i;
            bitPos++;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Unquantize(int val, int prec)
    {
        if (prec >= 8) return val;
        return (val << (8 - prec)) | (val >> (2 * prec - 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Bc7Interpolate(int ep0, int ep1, int weight)
        => (byte)(((64 - weight) * ep0 + weight * ep1 + 32) >> 6);

    private static int GetBc7Subset(int numSubsets, int partition, int texel)
    {
        if (numSubsets == 1) return 0;
        if (numSubsets == 2) return (Bc7Partition2[partition] >> texel) & 1;
        return (int)((Bc7Partition3[partition] >> (texel * 2)) & 3);
    }

    private static int FindAnchor(int numSubsets, int partition, int targetSubset)
    {
        for (int i = 0; i < 16; i++)
        {
            if (GetBc7Subset(numSubsets, partition, i) == targetSubset) return i;
        }
        return 0;
    }

    #endregion

    #region BC7 Encoding

    /// <summary>
    /// Encode an ImageFrame as BC7 compressed DDS with DX10 header.
    /// Uses mode 6 (7-bit color+alpha, 4-bit indices) for all blocks.
    /// </summary>
    public static byte[] EncodeBc7(ImageFrame image)
    {
        int w = (int)image.Columns;
        int h = (int)image.Rows;
        int channels = image.NumberOfChannels;
        bool hasAlpha = image.HasAlpha;

        int blocksX = (w + 3) / 4;
        int blocksY = (h + 3) / 4;
        int blockDataSize = blocksX * blocksY * 16;

        // DDS header (128) + DX10 header (20) + block data
        byte[] output = new byte[HeaderSize + 20 + blockDataSize];
        int pos = 0;

        // DDS header
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), DdsMagic); pos += 4;
        WriteU32LE(output, ref pos, 124);
        WriteU32LE(output, ref pos, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000); // CAPS|HEIGHT|WIDTH|PIXELFORMAT|LINEARSIZE
        WriteU32LE(output, ref pos, (uint)h);
        WriteU32LE(output, ref pos, (uint)w);
        WriteU32LE(output, ref pos, (uint)blockDataSize); // linear size
        WriteU32LE(output, ref pos, 0); // depth
        WriteU32LE(output, ref pos, 1); // mipmap count
        for (int i = 0; i < 11; i++) WriteU32LE(output, ref pos, 0);

        // Pixel format — FourCC = DX10
        WriteU32LE(output, ref pos, 32);
        WriteU32LE(output, ref pos, DDPF_FOURCC);
        WriteU32LE(output, ref pos, FOURCC_DX10);
        WriteU32LE(output, ref pos, 0); // bits
        WriteU32LE(output, ref pos, 0); // r mask
        WriteU32LE(output, ref pos, 0); // g mask
        WriteU32LE(output, ref pos, 0); // b mask
        WriteU32LE(output, ref pos, 0); // a mask

        // Caps
        WriteU32LE(output, ref pos, 0x1000);
        for (int i = 0; i < 4; i++) WriteU32LE(output, ref pos, 0);

        // DX10 extended header
        WriteU32LE(output, ref pos, DXGI_FORMAT_BC7_UNORM_SRGB);
        WriteU32LE(output, ref pos, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteU32LE(output, ref pos, 0); // misc flag
        WriteU32LE(output, ref pos, 1); // array size
        WriteU32LE(output, ref pos, 0); // misc flags 2

        // Encode blocks using mode 6
        Span<byte> blockPixels = stackalloc byte[64]; // 16 texels × RGBA

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                // Extract 4×4 block
                for (int py = 0; py < 4; py++)
                {
                    int sy = Math.Min(by * 4 + py, h - 1);
                    var row = image.GetPixelRow(sy);
                    for (int px = 0; px < 4; px++)
                    {
                        int sx = Math.Min(bx * 4 + px, w - 1);
                        int si = (py * 4 + px) * 4;
                        int di = sx * channels;
                        blockPixels[si] = Quantum.ScaleToByte(row[di]);
                        blockPixels[si + 1] = channels > 1 ? Quantum.ScaleToByte(row[di + 1]) : blockPixels[si];
                        blockPixels[si + 2] = channels > 2 ? Quantum.ScaleToByte(row[di + 2]) : blockPixels[si];
                        blockPixels[si + 3] = hasAlpha ? Quantum.ScaleToByte(row[di + channels - 1]) : (byte)255;
                    }
                }

                EncodeBc7Mode6Block(blockPixels, output.AsSpan(pos));
                pos += 16;
            }
        }

        return output;
    }

    /// <summary>
    /// Encode a 4×4 block using BC7 mode 6:
    /// 7-bit color + 7-bit alpha, unique p-bits, 4-bit indices, 1 subset.
    /// Mode bits: 0000001 (7 bits), no partition/rotation.
    /// </summary>
    private static void EncodeBc7Mode6Block(ReadOnlySpan<byte> texels, Span<byte> outBlock)
    {
        outBlock.Clear();

        // Find min/max for each channel
        int minR = 255, maxR = 0, minG = 255, maxG = 0;
        int minB = 255, maxB = 0, minA = 255, maxA = 0;
        for (int i = 0; i < 16; i++)
        {
            int r = texels[i * 4], g = texels[i * 4 + 1], b = texels[i * 4 + 2], a = texels[i * 4 + 3];
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
            if (a < minA) minA = a; if (a > maxA) maxA = a;
        }

        // Quantize endpoints to 7 bits (with p-bit providing 8th bit)
        int ep0R = minR >> 1, ep1R = maxR >> 1;
        int ep0G = minG >> 1, ep1G = maxG >> 1;
        int ep0B = minB >> 1, ep1B = maxB >> 1;
        int ep0A = minA >> 1, ep1A = maxA >> 1;

        // Try both p-bit values and pick the one with less total endpoint error
        int bestPbit0 = 0, bestPbit1 = 0, bestPbitErr = int.MaxValue;
        for (int p0 = 0; p0 <= 1; p0++)
        {
            for (int p1 = 0; p1 <= 1; p1++)
            {
                int err = 0;
                err += Sqr(((ep0R << 1) | p0) - minR) + Sqr(((ep1R << 1) | p1) - maxR);
                err += Sqr(((ep0G << 1) | p0) - minG) + Sqr(((ep1G << 1) | p1) - maxG);
                err += Sqr(((ep0B << 1) | p0) - minB) + Sqr(((ep1B << 1) | p1) - maxB);
                err += Sqr(((ep0A << 1) | p0) - minA) + Sqr(((ep1A << 1) | p1) - maxA);
                if (err < bestPbitErr) { bestPbitErr = err; bestPbit0 = p0; bestPbit1 = p1; }
            }
        }
        int pbit0 = bestPbit0, pbit1 = bestPbit1;

        // Reconstruct 8-bit endpoints for index assignment (7-bit << 1 | pbit = 8-bit)
        int r0Full = (ep0R << 1) | pbit0, r1Full = (ep1R << 1) | pbit1;
        int g0Full = (ep0G << 1) | pbit0, g1Full = (ep1G << 1) | pbit1;
        int b0Full = (ep0B << 1) | pbit0, b1Full = (ep1B << 1) | pbit1;
        int a0Full = (ep0A << 1) | pbit0, a1Full = (ep1A << 1) | pbit1;

        // Assign 4-bit indices (0-15) for each texel
        Span<byte> indices = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int r = texels[i * 4], g = texels[i * 4 + 1], b = texels[i * 4 + 2], a = texels[i * 4 + 3];
            int bestIdx = 0, bestErr = int.MaxValue;
            for (int idx = 0; idx < 16; idx++)
            {
                int w = Bc7Weights[4][idx];
                int pr = ((64 - w) * r0Full + w * r1Full + 32) >> 6;
                int pg = ((64 - w) * g0Full + w * g1Full + 32) >> 6;
                int pb = ((64 - w) * b0Full + w * b1Full + 32) >> 6;
                int pa = ((64 - w) * a0Full + w * a1Full + 32) >> 6;
                int err = (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb) + (a - pa) * (a - pa);
                if (err < bestErr) { bestErr = err; bestIdx = idx; }
            }
            indices[i] = (byte)bestIdx;
        }

        // Ensure anchor (texel 0) has MSB = 0; if not, swap endpoints
        if (indices[0] >= 8)
        {
            (ep0R, ep1R) = (ep1R, ep0R);
            (ep0G, ep1G) = (ep1G, ep0G);
            (ep0B, ep1B) = (ep1B, ep0B);
            (ep0A, ep1A) = (ep1A, ep0A);
            (pbit0, pbit1) = (pbit1, pbit0);
            for (int i = 0; i < 16; i++) indices[i] = (byte)(15 - indices[i]);
        }

        // Write mode 6 block: 7 mode bits + 7×4 color + 7×2 alpha + 2 pbits + 63 index bits = 128
        int bp = 0;
        WriteBc7Bits(outBlock, ref bp, 7, 0x40); // mode 6: bit pattern 0000001 (bit 6 set)
        // Color endpoints: R0, R1, G0, G1, B0, B1
        WriteBc7Bits(outBlock, ref bp, 7, ep0R);
        WriteBc7Bits(outBlock, ref bp, 7, ep1R);
        WriteBc7Bits(outBlock, ref bp, 7, ep0G);
        WriteBc7Bits(outBlock, ref bp, 7, ep1G);
        WriteBc7Bits(outBlock, ref bp, 7, ep0B);
        WriteBc7Bits(outBlock, ref bp, 7, ep1B);
        // Alpha endpoints
        WriteBc7Bits(outBlock, ref bp, 7, ep0A);
        WriteBc7Bits(outBlock, ref bp, 7, ep1A);
        // P-bits
        WriteBc7Bits(outBlock, ref bp, 1, pbit0);
        WriteBc7Bits(outBlock, ref bp, 1, pbit1);
        // Indices (texel 0 uses 3 bits, others use 4)
        WriteBc7Bits(outBlock, ref bp, 3, indices[0]);
        for (int i = 1; i < 16; i++)
            WriteBc7Bits(outBlock, ref bp, 4, indices[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteBc7Bits(Span<byte> block, ref int bitPos, int count, int value)
    {
        for (int i = 0; i < count; i++)
        {
            block[bitPos >> 3] |= (byte)(((value >> i) & 1) << (bitPos & 7));
            bitPos++;
        }
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeRgb565(ushort color, out byte r, out byte g, out byte b)
    {
        int ri = (color >> 11) & 0x1F;
        int gi = (color >> 5) & 0x3F;
        int bi = color & 0x1F;
        r = (byte)((ri << 3) | (ri >> 2));
        g = (byte)((gi << 2) | (gi >> 4));
        b = (byte)((bi << 3) | (bi >> 2));
    }

    private static void WriteBlockToFrame(ImageFrame frame,
        int blockX, int blockY, int width, int height,
        ReadOnlySpan<byte> colors, uint indices, int channels)
    {
        for (int py = 0;py < 4;py++)
        {
            int iy = blockY + py;
            if (iy >= height)
            {
                continue;
            }

            var row = frame.GetPixelRowForWrite(iy);

            for (int px = 0;px < 4;px++)
            {
                int ix = blockX + px;
                if (ix >= width)
                {
                    continue;
                }

                int pixelIdx = py * 4 + px;
                int colorIdx = (int)((indices >> (pixelIdx * 2)) & 3);
                int ci = colorIdx * 4;

                int offset = ix * channels;
                row[offset] = Quantum.ScaleFromByte(colors[ci]);
                if (channels > 1)
                {
                    row[offset + 1] = Quantum.ScaleFromByte(colors[ci + 1]);
                }

                if (channels > 2)
                {
                    row[offset + 2] = Quantum.ScaleFromByte(colors[ci + 2]);
                }

                row[offset + channels - 1] = Quantum.ScaleFromByte(colors[ci + 3]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ExpandBits(int value, int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        if (bits == 8)
        {
            return (byte)value;
        }

        return (byte)((value * 255) / ((1 << bits) - 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountTrailingZeros(uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        return System.Numerics.BitOperations.TrailingZeroCount(mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBits(uint mask)
    {
        return System.Numerics.BitOperations.PopCount(mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32LE(ReadOnlySpan<byte> data, ref int pos)
    {
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        pos += 4;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sqr(int x) => x * x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteU32LE(byte[] data, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), value);
        pos += 4;
    }

    #endregion
}
