using System;
using SharpImage.Compression;
using SharpImage.Core;
using SharpImage.Formats;
using SharpImage.Image;

namespace SharpImage.Tests.Formats;

public class JpegDctTests
{
    // Extracting a baseline JPEG's quantized DCT coefficients, then dequantising + inverse-DCT per component
    // and applying the same YCbCr->RGB, must reproduce the codec's own pixel decode exactly (4:4:4 => no
    // chroma subsampling, so every component shares one full-resolution block grid). This proves the
    // extracted coefficients are the JPEG's true quantized coefficients.
    [Test]
    public async Task Jpeg_DctExtraction_ReconstructsPixels()
    {
        const int w = 40, h = 24;
        var srcImg = new ImageFrame();
        srcImg.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = srcImg.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * srcImg.NumberOfChannels;
                row[o] = Quantum.ScaleFromByte((byte)((x * 7) ^ (y * 13)));
                row[o + 1] = Quantum.ScaleFromByte((byte)((x * 11) + (y * 5)));
                row[o + 2] = Quantum.ScaleFromByte((byte)(200 - (x * 3) - y));
            }
        }

        using var ms = new System.IO.MemoryStream();
        JpegCoder.Write(srcImg, ms, quality: 85, subsampling: JpegSubsampling.Yuv444);
        byte[] jpeg = ms.ToArray();

        ImageFrame reference = JpegCoder.Read(new System.IO.MemoryStream(jpeg));
        JpegDctData dct = JpegCoder.ReadDctData(new System.IO.MemoryStream(jpeg));

        await Assert.That(dct.Width).IsEqualTo(w);
        await Assert.That(dct.Height).IsEqualTo(h);
        await Assert.That(dct.ComponentCount).IsEqualTo(3);

        // Reconstruct each component's spatial samples from the extracted coefficients (4:4:4 => 1 block grid).
        var spatial = new int[3][]; // [component][y*w+x]
        for (int c = 0; c < 3; c++)
        {
            JpegDctComponent comp = dct.Components[c];
            int[] qt = dct.QuantTables[comp.QuantTableIndex];
            int bpr = comp.BlocksPerRow;
            var plane = new int[w * h];
            spatial[c] = plane;
            for (int by = 0; by < comp.BlocksPerCol; by++)
            {
                for (int bx = 0; bx < bpr; bx++)
                {
                    var block = (int[])comp.Blocks[(by * bpr) + bx].Clone();
                    for (int i = 0; i < 64; i++)
                    {
                        block[i] *= qt[i];
                    }

                    Dct.InverseDct(block);
                    for (int py = 0; py < 8; py++)
                    {
                        int y = (by * 8) + py;
                        if (y >= h)
                        {
                            break;
                        }

                        for (int px = 0; px < 8; px++)
                        {
                            int x = (bx * 8) + px;
                            if (x < w)
                            {
                                plane[(y * w) + x] = block[(py * 8) + px];
                            }
                        }
                    }
                }
            }
        }

        bool match = true;
        for (int y = 0; y < h && match; y++)
        {
            var refRow = reference.GetPixelRow(y);
            for (int x = 0; x < w; x++)
            {
                int yVal = spatial[0][(y * w) + x] + 128;
                int cb = spatial[1][(y * w) + x];
                int cr = spatial[2][(y * w) + x];
                int r = Math.Clamp(yVal + (((cr * 91881) + 32768) >> 16), 0, 255);
                int g = Math.Clamp(yVal - (((cb * 22554) + (cr * 46802) + 32768) >> 16), 0, 255);
                int b = Math.Clamp(yVal + (((cb * 116130) + 32768) >> 16), 0, 255);
                int o = x * reference.NumberOfChannels;
                if (Quantum.ScaleToByte(refRow[o]) != r || Quantum.ScaleToByte(refRow[o + 1]) != g || Quantum.ScaleToByte(refRow[o + 2]) != b)
                {
                    match = false;
                    break;
                }
            }
        }

        await Assert.That(match).IsTrue();
    }

    // Byte-exact JPEG reconstruction (jbrd core): ReadDctData -> RebuildJpeg reproduces the original baseline
    // JPEG byte-for-byte, across subsampling modes, sizes and restart intervals.
    [Test]
    [Arguments(40, 24, JpegSubsampling.Yuv444, 0)]
    [Arguments(64, 48, JpegSubsampling.Yuv420, 0)]
    [Arguments(48, 40, JpegSubsampling.Yuv422, 0)]
    [Arguments(80, 64, JpegSubsampling.Yuv420, 3)]
    public async Task Jpeg_ByteExact_RoundTrips(int w, int h, JpegSubsampling ss, int restartMcus)
    {
        var src = new ImageFrame();
        src.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * src.NumberOfChannels;
                row[o] = Quantum.ScaleFromByte((byte)((x * 7) ^ (y * 13)));
                row[o + 1] = Quantum.ScaleFromByte((byte)((x * 11) + (y * 5)));
                row[o + 2] = Quantum.ScaleFromByte((byte)(200 - (x * 3) - y));
            }
        }

        using var ms = new System.IO.MemoryStream();
        JpegCoder.Write(src, ms, quality: 80, subsampling: ss);
        byte[] original = ms.ToArray();

        JpegDctData dct = JpegCoder.ReadDctData(new System.IO.MemoryStream(original));
        byte[] rebuilt = JpegCoder.RebuildJpeg(dct);

        await Assert.That(rebuilt.Length).IsEqualTo(original.Length);
        await Assert.That(rebuilt.AsSpan().SequenceEqual(original)).IsTrue();
    }

    // End-to-end lossless JPEG -> JXL-recompression container -> JPEG reproduces the original JPEG byte-exact.
    [Test]
    [Arguments(64, 48, JpegSubsampling.Yuv444)]
    [Arguments(80, 64, JpegSubsampling.Yuv420)]
    [Arguments(48, 40, JpegSubsampling.Yuv422)]
    public async Task Jpeg_LosslessTranscode_RoundTrips(int w, int h, JpegSubsampling ss)
    {
        var src = new ImageFrame();
        src.Initialize(w, h, ColorspaceType.SRGB, false);
        for (int y = 0; y < h; y++)
        {
            var row = src.GetPixelRowForWrite(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * src.NumberOfChannels;
                row[o] = Quantum.ScaleFromByte((byte)((x * 7) ^ (y * 13)));
                row[o + 1] = Quantum.ScaleFromByte((byte)((x * 11) + (y * 5)));
                row[o + 2] = Quantum.ScaleFromByte((byte)(200 - (x * 3) - y));
            }
        }

        using var ms = new System.IO.MemoryStream();
        JpegCoder.Write(src, ms, quality: 82, subsampling: ss);
        byte[] jpeg = ms.ToArray();

        byte[] container = JpegXlLossless.Encode(jpeg);
        await Assert.That(JpegXlLossless.CanDecode(container)).IsTrue();
        byte[] restored = JpegXlLossless.Decode(container);

        await Assert.That(restored.Length).IsEqualTo(jpeg.Length);
        await Assert.That(restored.AsSpan().SequenceEqual(jpeg)).IsTrue();
    }

    // A real PROGRESSIVE (SOF2) JPEG — multi-scan DC first/refine + AC first/refine with EOBRUN grouping —
    // must recompress and reconstruct byte-for-byte. This embeds a small libjpeg-family progressive JPEG so
    // the coverage runs everywhere (no external corpus needed). Guards the progressive scan re-encoder and the
    // AC-refine EOBRUN decode fix.
    [Test]
    public async Task Jpeg_Progressive_ByteExact_RoundTrips()
    {
        byte[] jpeg = Convert.FromBase64String(ProgressiveJpegBase64);

        // Sanity: it really is progressive (contains an SOF2 marker).
        bool isProgressive = false;
        for (int i = 2; i < jpeg.Length - 1; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC2)
            {
                isProgressive = true;
                break;
            }
        }

        await Assert.That(isProgressive).IsTrue();

        byte[] container = JpegXlLossless.Encode(jpeg); // self-verifies byte-exactness internally
        byte[] restored = JpegXlLossless.Decode(container);
        await Assert.That(restored.AsSpan().SequenceEqual(jpeg)).IsTrue();
    }

    private const string ProgressiveJpegBase64 =
        "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";

    // Byte-exact round-trip of REAL third-party JPEGs (libjpeg/ffmpeg) from VARDCT_JBRD_DIR, which exercise
    // real Huffman tables, APPn/COM segments and restart markers.
    [Test]
    public async Task Jpeg_ByteExact_RealFiles()
    {
        string dir = Environment.GetEnvironmentVariable("VARDCT_JBRD_DIR");
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
        {
            return;
        }

        var report = new System.Text.StringBuilder();
        int tested = 0, ok = 0;
        foreach (string f in System.IO.Directory.GetFiles(dir, "*.jpg"))
        {
            byte[] original = System.IO.File.ReadAllBytes(f);
            tested++;
            try
            {
                JpegDctData dct = JpegCoder.ReadDctData(new System.IO.MemoryStream(original));
                byte[] rebuilt = JpegCoder.RebuildJpeg(dct);
                bool exact = rebuilt.AsSpan().SequenceEqual(original);
                if (exact)
                {
                    ok++;
                }
                else
                {
                    int firstDiff = 0;
                    int n = Math.Min(rebuilt.Length, original.Length);
                    while (firstDiff < n && rebuilt[firstDiff] == original[firstDiff]) firstDiff++;
                    report.AppendLine($"{System.IO.Path.GetFileName(f)}: MISMATCH len {original.Length}->{rebuilt.Length} firstDiff@{firstDiff}");
                }
            }
            catch (Exception e)
            {
                report.AppendLine($"{System.IO.Path.GetFileName(f)}: THREW {e.GetType().Name}: {e.Message}");
            }
        }

        report.Insert(0, $"tested={tested} ok={ok}\n");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "jbrd_result.txt"), report.ToString());
        await Assert.That(ok).IsEqualTo(tested);
    }
}
