using System.Buffers.Binary;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// 32-bpp BMPs as the Windows clipboard and screen captures write them: BGRX with an unused
/// (zero) fourth byte, real alpha, and bit-field masks after a v3 header or inside a v5 header.
/// </summary>
public class BmpAlphaAndMaskTests
{
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;

    [Test]
    public async Task Rgb32_WithZeroAlphaEverywhere_IsOpaque()
    {
        // Two pixels, BGRX: red and green, fourth byte 0.
        byte[] bmp = Build(40, BiRgb, masks: null, pixels: [0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00]);

        using ImageFrame frame = BmpCoder.Read(new MemoryStream(bmp));

        await AssertPixel(frame, 0, 255, 0, 0, 255);
        await AssertPixel(frame, 1, 0, 255, 0, 255);
    }

    [Test]
    public async Task Rgb32_WithRealAlpha_KeepsIt()
    {
        byte[] bmp = Build(40, BiRgb, masks: null, pixels: [0x00, 0x00, 0xFF, 0x80, 0x00, 0xFF, 0x00, 0x00]);

        using ImageFrame frame = BmpCoder.Read(new MemoryStream(bmp));

        await AssertPixel(frame, 0, 255, 0, 0, 0x80);
        await AssertPixel(frame, 1, 0, 255, 0, 0);
    }

    [Test]
    public async Task V3Header_Bitfields_ReadsMasksAfterHeader()
    {
        // RGBX byte order: R mask 0x000000FF, G 0x0000FF00, B 0x00FF0000. Pixel 0 red, pixel 1 blue.
        byte[] bmp = Build(40, BiBitfields, masks: [0x000000FF, 0x0000FF00, 0x00FF0000],
            pixels: [0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00]);

        using ImageFrame frame = BmpCoder.Read(new MemoryStream(bmp));

        await Assert.That(frame.HasAlpha).IsFalse();
        await AssertPixel(frame, 0, 255, 0, 0, 255);
        await AssertPixel(frame, 1, 0, 0, 255, 255);
    }

    [Test]
    public async Task V5Header_Bitfields_WithAlphaMask_KeepsAlpha()
    {
        byte[] bmp = Build(124, BiBitfields, masks: [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000],
            pixels: [0xFF, 0x00, 0x00, 0x40, 0x00, 0xFF, 0x00, 0xFF]);

        using ImageFrame frame = BmpCoder.Read(new MemoryStream(bmp));

        await AssertPixel(frame, 0, 0, 0, 255, 0x40);
        await AssertPixel(frame, 1, 0, 255, 0, 255);
    }

    // A 2×1 top-down 32-bpp BMP. Masks go inside a v4/v5 header, or after a v3 header.
    private static byte[] Build(int headerSize, uint compression, uint[]? masks, byte[] pixels)
    {
        int maskBytes = headerSize == 40 && masks is not null ? masks.Length * 4 : 0;
        int offset = 14 + headerSize + maskBytes;
        byte[] data = new byte[offset + pixels.Length];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), offset);

        Span<byte> header = data.AsSpan(14, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], -1);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], compression);

        if (masks is not null)
        {
            Span<byte> maskTarget = headerSize == 40 ? data.AsSpan(14 + 40) : header[40..];
            for (int i = 0; i < masks.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(maskTarget[(i * 4)..], masks[i]);
            }
        }
        pixels.CopyTo(data.AsSpan(offset));
        return data;
    }

    private static async Task AssertPixel(ImageFrame frame, int x, byte r, byte g, byte b, byte a)
    {
        ushort[] row = frame.GetPixelRow(0).ToArray();
        int c = frame.NumberOfChannels;
        await Assert.That(Quantum.ScaleToByte(row[x * c])).IsEqualTo(r);
        await Assert.That(Quantum.ScaleToByte(row[x * c + 1])).IsEqualTo(g);
        await Assert.That(Quantum.ScaleToByte(row[x * c + 2])).IsEqualTo(b);
        byte alpha = frame.HasAlpha ? Quantum.ScaleToByte(row[x * c + c - 1]) : (byte)255;
        await Assert.That(alpha).IsEqualTo(a);
    }
}
