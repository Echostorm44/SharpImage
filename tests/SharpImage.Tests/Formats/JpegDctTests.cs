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
}
