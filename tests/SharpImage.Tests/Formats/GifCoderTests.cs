using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

/// <summary>
/// Tests for GIF format coder and LZW compression.
/// </summary>
public class GifCoderTests
{
    private static readonly string TestImagesDir = Path.Combine(AppContext.BaseDirectory, "TestAssets");

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"sharpimage_test_{name}");

    // ─── LZW Tests ───────────────────────────────────────────────

    [Test]
    public async Task Lzw_RoundTrip_PreservesData()
    {
        byte[] original = [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 0, 0, 1, 1];
        using var compressed = new MemoryStream();
        Lzw.Compress(compressed, original, 2); // minCodeSize=2 (for 4 values 0-3)

        compressed.Position = 0;
        byte[] decompressed = Lzw.Decompress(compressed, 2);

        await Assert.That(decompressed.Length).IsEqualTo(original.Length);
        for (int i = 0; i < original.Length; i++)
            await Assert.That(decompressed[i]).IsEqualTo(original[i]);
    }

    [Test]
    public async Task Lzw_LargerData_RoundTrip()
    {
        // Create repetitive data that LZW can compress well
        byte[] data = new byte[1024];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 16);

        using var compressed = new MemoryStream();
        Lzw.Compress(compressed, data, 4); // minCodeSize=4 for 16 values

        compressed.Position = 0;
        byte[] decompressed = Lzw.Decompress(compressed, 4);

        await Assert.That(decompressed.Length).IsEqualTo(data.Length);
        for (int i = 0; i < data.Length; i++)
            await Assert.That(decompressed[i]).IsEqualTo(data[i]);
    }

    // ─── GIF Round-Trip Tests ────────────────────────────────────

    [Test]
    public async Task Gif_RoundTrip_SmallImage()
    {
        var original = CreateSimpleImage(8, 6);
        string tempFile = TempPath("roundtrip.gif");

        try
        {
            GifCoder.Write(original, tempFile);
            var loaded = GifCoder.Read(tempFile);

            await Assert.That(loaded.Columns).IsEqualTo(8);
            await Assert.That(loaded.Rows).IsEqualTo(6);

            // GIF quantizes to 256 colors, so check basic color preservation
            for (int y = 0; y < 6; y++)
            {
                var origRow = original.GetPixelRow(y).ToArray();
                var loadRow = loaded.GetPixelRow(y).ToArray();
                int channels = original.NumberOfChannels;
                int loadChannels = loaded.NumberOfChannels;

                for (int x = 0; x < 8; x++)
                {
                    // Allow generous tolerance due to palette quantization
                    for (int c = 0; c < 3; c++)
                    {
                        int origByte = Quantum.ScaleToByte(origRow[x * channels + c]);
                        int loadByte = Quantum.ScaleToByte(loadRow[x * loadChannels + c]);
                        int diff = Math.Abs(origByte - loadByte);
                        await Assert.That(diff).IsLessThanOrEqualTo(2);
                    }
                }
            }
            loaded.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Real File Tests ─────────────────────────────────────────

    [Test]
    public async Task Gif_Read_SmileImage_CorrectDimensions()
    {
        string path = Path.Combine(TestImagesDir, "bounce_anim.gif");
        var image = GifCoder.Read(path);
        try
        {
            await Assert.That(image.Columns).IsGreaterThan(0);
            await Assert.That(image.Rows).IsGreaterThan(0);
        }
        finally
        {
            image.Dispose();
        }
    }

    [Test]
    public async Task Gif_Read_ButtonImage_HasPixels()
    {
        string path = Path.Combine(TestImagesDir, "toggle_button.gif");
        var image = GifCoder.Read(path);
        try
        {
            await Assert.That(image.Columns).IsGreaterThan(0);
            // Verify non-black pixels exist
            var row = image.GetPixelRow(image.Rows / 2).ToArray();
            bool hasContent = false;
            for (int i = 0; i < row.Length; i++)
                if (row[i] > 0) { hasContent = true; break; }
            await Assert.That(hasContent).IsTrue();
        }
        finally
        {
            image.Dispose();
        }
    }

    // ─── Cross-Format Tests ──────────────────────────────────────

    [Test]
    public async Task Gif_ToPng_CrossFormat()
    {
        var original = CreateSimpleImage(12, 8);
        string gifFile = TempPath("cross.gif");
        string pngFile = TempPath("from_gif.png");

        try
        {
            GifCoder.Write(original, gifFile);
            var fromGif = GifCoder.Read(gifFile);

            PngCoder.Write(fromGif, pngFile);
            var fromPng = PngCoder.Read(pngFile);

            await Assert.That(fromPng.Columns).IsEqualTo(12);
            await Assert.That(fromPng.Rows).IsEqualTo(8);

            fromGif.Dispose();
            fromPng.Dispose();
        }
        finally
        {
            original.Dispose();
            if (File.Exists(gifFile)) File.Delete(gifFile);
            if (File.Exists(pngFile)) File.Delete(pngFile);
        }
    }

    // ─── Signature Tests ─────────────────────────────────────────

    [Test]
    public async Task Gif_FileWritten_HasValidSignature()
    {
        var image = CreateSimpleImage(4, 4);
        string tempFile = TempPath("signature.gif");

        try
        {
            GifCoder.Write(image, tempFile);
            byte[] bytes = File.ReadAllBytes(tempFile);

            // GIF89a signature
            await Assert.That(bytes[0]).IsEqualTo((byte)'G');
            await Assert.That(bytes[1]).IsEqualTo((byte)'I');
            await Assert.That(bytes[2]).IsEqualTo((byte)'F');
            await Assert.That(bytes[3]).IsEqualTo((byte)'8');
            await Assert.That(bytes[4]).IsEqualTo((byte)'9');
            await Assert.That(bytes[5]).IsEqualTo((byte)'a');

            // Should end with trailer 0x3B
            await Assert.That(bytes[^1]).IsEqualTo((byte)0x3B);
        }
        finally
        {
            image.Dispose();
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Gif_Read_InvalidData_Throws()
    {
        string tempFile = TempPath("invalid.gif");
        try
        {
            File.WriteAllBytes(tempFile, [0, 0, 0, 0, 0, 0]);
            await Assert.That(() => GifCoder.Read(tempFile)).Throws<InvalidDataException>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Helper ──────────────────────────────────────────────────

    /// <summary>Creates a simple image with distinct flat colors (good for GIF palette).</summary>
    private static ImageFrame CreateSimpleImage(int width, int height)
    {
        var image = new ImageFrame();
        image.Initialize(width, height, ColorspaceType.SRGB, false);

        int channels = image.NumberOfChannels;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetPixelRowForWrite(y);
            for (int x = 0; x < width; x++)
            {
                int offset = x * channels;
                // Use flat colors that survive palette quantization perfectly
                byte r = (byte)((x * 85) % 256);
                byte g = (byte)((y * 85) % 256);
                byte b = (byte)(((x + y) * 42) % 256);
                row[offset] = Quantum.ScaleFromByte(r);
                row[offset + 1] = Quantum.ScaleFromByte(g);
                row[offset + 2] = Quantum.ScaleFromByte(b);
            }
        }
        return image;
    }
}
