// JPEG XL VarDCT transforms: the 27 varblock transform types, the recursive DCT/IDCT for all sizes,
// and the special transforms (DCT2, DCT4, Hornuss, DCT4x8/8x4, AFV0-3). Ported from jxl-oxide
// (jxl-render vardct/dct.rs, transform.rs, transform_common.rs, dct_common.rs) and libjxl.
using System;

namespace SharpImage.Formats.Jxl;

internal enum TransformType
{
    Dct8 = 0, Hornuss, Dct2, Dct4, Dct16, Dct32, Dct16x8, Dct8x16, Dct32x8, Dct8x32,
    Dct32x16, Dct16x32, Dct4x8, Dct8x4, Afv0, Afv1, Afv2, Afv3, Dct64, Dct64x32,
    Dct32x64, Dct128, Dct128x64, Dct64x128, Dct256, Dct256x128, Dct128x256,
}

internal static class JxlDct
{
    // Transform size in 8x8 blocks (width, height).
    public static (int W, int H) DctSelectSize(TransformType t) => t switch
    {
        TransformType.Dct8 or TransformType.Hornuss or TransformType.Dct2 or TransformType.Dct4
            or TransformType.Dct4x8 or TransformType.Dct8x4 or TransformType.Afv0 or TransformType.Afv1
            or TransformType.Afv2 or TransformType.Afv3 => (1, 1),
        TransformType.Dct16 => (2, 2),
        TransformType.Dct32 => (4, 4),
        TransformType.Dct16x8 => (1, 2),
        TransformType.Dct8x16 => (2, 1),
        TransformType.Dct32x8 => (1, 4),
        TransformType.Dct8x32 => (4, 1),
        TransformType.Dct32x16 => (2, 4),
        TransformType.Dct16x32 => (4, 2),
        TransformType.Dct64 => (8, 8),
        TransformType.Dct64x32 => (4, 8),
        TransformType.Dct32x64 => (8, 4),
        TransformType.Dct128 => (16, 16),
        TransformType.Dct128x64 => (8, 16),
        TransformType.Dct64x128 => (16, 8),
        TransformType.Dct256 => (32, 32),
        TransformType.Dct256x128 => (16, 32),
        TransformType.Dct128x256 => (32, 16),
        _ => (1, 1),
    };

    // Dequant matrix pixel size (width, height).
    public static (int W, int H) DequantMatrixSize(TransformType t) => t switch
    {
        TransformType.Dct8 or TransformType.Hornuss or TransformType.Dct2 or TransformType.Dct4
            or TransformType.Dct4x8 or TransformType.Dct8x4 or TransformType.Afv0 or TransformType.Afv1
            or TransformType.Afv2 or TransformType.Afv3 => (8, 8),
        TransformType.Dct16 => (16, 16),
        TransformType.Dct32 => (32, 32),
        TransformType.Dct16x8 or TransformType.Dct8x16 => (16, 8),
        TransformType.Dct32x8 or TransformType.Dct8x32 => (32, 8),
        TransformType.Dct32x16 or TransformType.Dct16x32 => (32, 16),
        TransformType.Dct64 => (64, 64),
        TransformType.Dct64x32 or TransformType.Dct32x64 => (64, 32),
        TransformType.Dct128 => (128, 128),
        TransformType.Dct128x64 or TransformType.Dct64x128 => (128, 64),
        TransformType.Dct256 => (256, 256),
        TransformType.Dct256x128 or TransformType.Dct128x256 => (256, 128),
        _ => (8, 8),
    };

    public static int OrderId(TransformType t) => t switch
    {
        TransformType.Dct8 => 0,
        TransformType.Hornuss or TransformType.Dct2 or TransformType.Dct4 or TransformType.Dct4x8
            or TransformType.Dct8x4 or TransformType.Afv0 or TransformType.Afv1 or TransformType.Afv2
            or TransformType.Afv3 => 1,
        TransformType.Dct16 => 2,
        TransformType.Dct32 => 3,
        TransformType.Dct16x8 or TransformType.Dct8x16 => 4,
        TransformType.Dct32x8 or TransformType.Dct8x32 => 5,
        TransformType.Dct32x16 or TransformType.Dct16x32 => 6,
        TransformType.Dct64 => 7,
        TransformType.Dct64x32 or TransformType.Dct32x64 => 8,
        TransformType.Dct128 => 9,
        TransformType.Dct128x64 or TransformType.Dct64x128 => 10,
        TransformType.Dct256 => 11,
        TransformType.Dct256x128 or TransformType.Dct128x256 => 12,
        _ => 0,
    };

    public static bool NeedTranspose(TransformType t)
    {
        switch (t)
        {
            case TransformType.Hornuss:
            case TransformType.Dct2:
            case TransformType.Dct4:
            case TransformType.Dct4x8:
            case TransformType.Dct8x4:
            case TransformType.Afv0:
            case TransformType.Afv1:
            case TransformType.Afv2:
            case TransformType.Afv3:
                return false;
            default:
                var (w, h) = DctSelectSize(t);
                return h >= w;
        }
    }

    // --- DCT scale factors ---
    private static readonly float[] ScaleFTable =
    {
        1.0000000000000000f, 0.9996047255830407f, 0.9984194528776054f, 0.9964458326264695f, 0.9936866130906366f,
        0.9901456355893141f, 0.9858278282666936f, 0.9807391980963174f, 0.9748868211368796f, 0.9682788310563117f,
        0.9609244059440204f, 0.9528337534340876f, 0.9440180941651672f, 0.9344896436056892f, 0.9242615922757944f,
        0.9133480844001980f, 0.9017641950288744f, 0.8895259056651056f, 0.8766500784429904f, 0.8631544288990163f,
        0.8490574973847023f, 0.8343786191696513f, 0.8191378932865928f, 0.8033561501721485f, 0.7870549181591013f,
        0.7702563888779096f, 0.7529833816270532f, 0.7352593067735488f, 0.7171081282466044f, 0.6985543251889097f,
        0.6796228528314652f, 0.6603391026591464f,
    };

    private static float ScaleF(int c, int logb) => ScaleFTable[c << logb];

    private static readonly float[][] SecHalfSmall =
    {
        new[] { 0.541196100146197f, 1.3065629648763764f },
        new[] { 0.5097955791041592f, 0.6013448869350453f, 0.8999762231364156f, 2.5629154477415055f },
        new[] { 0.5024192861881557f, 0.5224986149396889f, 0.5669440348163577f, 0.6468217833599901f, 0.7881546234512502f, 1.060677685990347f, 1.7224470982383342f, 5.101148618689155f },
        new[] { 0.5006029982351963f, 0.5054709598975436f, 0.5154473099226246f, 0.5310425910897841f, 0.5531038960344445f, 0.5829349682061339f, 0.6225041230356648f, 0.6748083414550057f, 0.7445362710022984f, 0.8393496454155268f, 0.9725682378619608f, 1.1694399334328847f, 1.4841646163141662f, 2.057781009953411f, 3.407608418468719f, 10.190008123548033f },
    };

    private static readonly System.Collections.Generic.Dictionary<int, float[]> SecHalfLarge = new();

    private static float[] SecHalf(int n)
    {
        int idx = System.Numerics.BitOperations.TrailingZeroCount(n) - 2;
        if (idx < 4)
        {
            return SecHalfSmall[idx];
        }

        if (!SecHalfLarge.TryGetValue(n, out float[]? table))
        {
            table = new float[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                float theta = ((2 * k) + 1) / (float)(2 * n) * MathF.PI;
                table[k] = 1f / MathF.Cos(theta) / 2f;
            }

            SecHalfLarge[n] = table;
        }

        return table;
    }

    // A mutable 2D subgrid view over a float buffer (row-major, arbitrary stride).
    internal struct Grid
    {
        public float[] Buf;
        public int Off;
        public int Stride;
        public int W;
        public int H;

        public Grid(float[] buf, int off, int stride, int w, int h)
        {
            Buf = buf; Off = off; Stride = stride; W = w; H = h;
        }

        public float Get(int x, int y) => Buf[Off + (y * Stride) + x];

        public void Set(int x, int y, float v) => Buf[Off + (y * Stride) + x] = v;

        public Grid Sub(int x0, int y0, int w, int h) => new(Buf, Off + (y0 * Stride) + x0, Stride, w, h);
    }

    private enum Dir { Forward, Inverse }

    private static float[] Dct4(float[] input, Dir dir)
    {
        const float sec0 = 0.5411961f;
        const float sec1 = 1.306563f;
        float sqrt2 = MathF.Sqrt(2f);
        if (dir == Dir.Forward)
        {
            float sum03 = input[0] + input[3];
            float sum12 = input[1] + input[2];
            float tmp0 = (input[0] - input[3]) * sec0;
            float tmp1 = (input[1] - input[2]) * sec1;
            float out0 = (tmp0 + tmp1) / 4f;
            float out1 = (tmp0 - tmp1) / 4f;
            return new[] { (sum03 + sum12) / 4f, (out0 * sqrt2) + out1, (sum03 - sum12) / 4f, out1 };
        }
        else
        {
            float tmp0 = input[1] * sqrt2;
            float tmp1 = input[1] + input[3];
            float out0 = (tmp0 + tmp1) * sec0;
            float out1 = (tmp0 - tmp1) * sec1;
            float sum02 = input[0] + input[2];
            float sub02 = input[0] - input[2];
            return new[] { sum02 + out0, sub02 + out1, sub02 - out1, sum02 - out0 };
        }
    }

    private static void Dct1D(float[] io, int off, int n, float[] scratch, Dir dir)
    {
        float sqrt2 = MathF.Sqrt(2f);
        if (n <= 1)
        {
            return;
        }

        if (n == 2)
        {
            float t0 = io[off] + io[off + 1];
            float t1 = io[off] - io[off + 1];
            if (dir == Dir.Forward)
            {
                io[off] = t0 / 2f;
                io[off + 1] = t1 / 2f;
            }
            else
            {
                io[off] = t0;
                io[off + 1] = t1;
            }

            return;
        }

        if (n == 4)
        {
            float[] r = Dct4(new[] { io[off], io[off + 1], io[off + 2], io[off + 3] }, dir);
            Array.Copy(r, 0, io, off, 4);
            return;
        }

        if (n == 8)
        {
            float[] sec = SecHalfSmall[1];
            if (dir == Dir.Forward)
            {
                float[] in0 = { (io[off] + io[off + 7]) / 2f, (io[off + 1] + io[off + 6]) / 2f, (io[off + 2] + io[off + 5]) / 2f, (io[off + 3] + io[off + 4]) / 2f };
                float[] in1 = { (io[off] - io[off + 7]) * sec[0] / 2f, (io[off + 1] - io[off + 6]) * sec[1] / 2f, (io[off + 2] - io[off + 5]) * sec[2] / 2f, (io[off + 3] - io[off + 4]) * sec[3] / 2f };
                float[] o0 = Dct4(in0, Dir.Forward);
                for (int i = 0; i < 4; i++)
                {
                    io[off + (i * 2)] = o0[i];
                }

                float[] o1 = Dct4(in1, Dir.Forward);
                o1[0] *= sqrt2;
                for (int i = 0; i < 3; i++)
                {
                    io[off + (i * 2) + 1] = o1[i] + o1[i + 1];
                }

                io[off + 7] = o1[3];
            }
            else
            {
                float[] in0 = { io[off], io[off + 2], io[off + 4], io[off + 6] };
                float[] in1 = { io[off + 1] * sqrt2, io[off + 3] + io[off + 1], io[off + 5] + io[off + 3], io[off + 7] + io[off + 5] };
                float[] o0 = Dct4(in0, Dir.Inverse);
                float[] o1 = Dct4(in1, Dir.Inverse);
                for (int i = 0; i < 4; i++)
                {
                    float r = o1[i] * sec[i];
                    io[off + i] = o0[i] + r;
                    io[off + 7 - i] = o0[i] - r;
                }
            }

            return;
        }

        // n >= 16, power of two: recursive.
        float[] input0 = new float[n / 2];
        float[] input1 = new float[n / 2];
        float[] sec2 = SecHalf(n);
        if (dir == Dir.Forward)
        {
            for (int i = 0; i < n / 2; i++)
            {
                input0[i] = (io[off + i] + io[off + n - i - 1]) / 2f;
                input1[i] = (io[off + i] - io[off + n - i - 1]) / 2f;
            }

            for (int i = 0; i < n / 2; i++)
            {
                input1[i] *= sec2[i];
            }

            Dct1D(input0, 0, n / 2, scratch, Dir.Forward);
            Dct1D(input1, 0, n / 2, scratch, Dir.Forward);
            input1[0] *= sqrt2;
            for (int i = 0; i < (n / 2) - 1; i++)
            {
                input1[i] += input1[i + 1];
            }

            for (int i = 0; i < n / 2; i++)
            {
                io[off + (i * 2)] = input0[i];
                io[off + (i * 2) + 1] = input1[i];
            }
        }
        else
        {
            for (int i = 0; i < n / 2; i++)
            {
                input0[i] = io[off + (i * 2)];
                input1[i] = io[off + (i * 2) + 1];
            }

            for (int i = 1; i < n / 2; i++)
            {
                input1[(n / 2) - i] += input1[(n / 2) - i - 1];
            }

            input1[0] *= sqrt2;
            Dct1D(input0, 0, n / 2, scratch, Dir.Inverse);
            Dct1D(input1, 0, n / 2, scratch, Dir.Inverse);
            for (int i = 0; i < n / 2; i++)
            {
                input1[i] *= sec2[i];
            }

            for (int i = 0; i < n / 2; i++)
            {
                io[off + i] = input0[i] + input1[i];
                io[off + n - i - 1] = input0[i] - input1[i];
            }
        }
    }

    private static void Swap(Grid g, int ax, int ay, int bx, int by)
    {
        int ia = g.Off + (ay * g.Stride) + ax;
        int ib = g.Off + (by * g.Stride) + bx;
        (g.Buf[ia], g.Buf[ib]) = (g.Buf[ib], g.Buf[ia]);
    }

    private static void RowInto(Grid g, int y, float[] tmp)
    {
        for (int x = 0; x < g.W; x++)
        {
            tmp[x] = g.Get(x, y);
        }
    }

    public static void Dct2D(Grid io, bool inverse)
    {
        Dir dir = inverse ? Dir.Inverse : Dir.Forward;
        int width = io.W, height = io.H;
        if (width * height <= 1)
        {
            return;
        }

        float mul = dir == Dir.Forward ? 0.5f : 1.0f;
        if (width == 2 && height == 1)
        {
            float v0 = io.Get(0, 0), v1 = io.Get(1, 0);
            io.Set(0, 0, (v0 + v1) * mul);
            io.Set(1, 0, (v0 - v1) * mul);
            return;
        }

        if (width == 1 && height == 2)
        {
            float v0 = io.Get(0, 0), v1 = io.Get(0, 1);
            io.Set(0, 0, (v0 + v1) * mul);
            io.Set(0, 1, (v0 - v1) * mul);
            return;
        }

        if (width == 2 && height == 2)
        {
            float v00 = io.Get(0, 0), v01 = io.Get(1, 0), v10 = io.Get(0, 1), v11 = io.Get(1, 1);
            io.Set(0, 0, (v00 + v01 + v10 + v11) * mul * mul);
            io.Set(1, 0, (v00 - v01 + v10 - v11) * mul * mul);
            io.Set(0, 1, (v00 + v01 - v10 - v11) * mul * mul);
            io.Set(1, 1, (v00 - v01 - v10 + v11) * mul * mul);
            return;
        }

        float[] buf = new float[Math.Max(width, height)];

        if (height == 1)
        {
            float[] row = new float[width];
            RowInto(io, 0, row);
            Dct1D(row, 0, width, buf, dir);
            for (int x = 0; x < width; x++)
            {
                io.Set(x, 0, row[x]);
            }

            return;
        }

        if (width == 1)
        {
            float[] col = new float[height];
            for (int y = 0; y < height; y++)
            {
                col[y] = io.Get(0, y);
            }

            Dct1D(col, 0, height, buf, dir);
            for (int y = 0; y < height; y++)
            {
                io.Set(0, y, col[y]);
            }

            return;
        }

        // General: rows, transpose-in-blocks, columns, transpose back (matches jxl-oxide).
        float[] rowbuf = new float[width];
        for (int y = 0; y < height; y++)
        {
            RowInto(io, y, rowbuf);
            Dct1D(rowbuf, 0, width, buf, dir);
            for (int x = 0; x < width; x++)
            {
                io.Set(x, y, rowbuf[x]);
            }
        }

        int blockSize = Math.Min(width, height);
        TransposeBlocks(io, blockSize);

        if (blockSize == height)
        {
            float[] rr = new float[height];
            for (int y = 0; y < height; y++)
            {
                for (int cx = 0; cx < width; cx += height)
                {
                    for (int i = 0; i < height; i++)
                    {
                        rr[i] = io.Get(cx + i, y);
                    }

                    Dct1D(rr, 0, height, buf, dir);
                    for (int i = 0; i < height; i++)
                    {
                        io.Set(cx + i, y, rr[i]);
                    }
                }
            }
        }
        else
        {
            float[] row = new float[height];
            for (int y = 0; y < width; y++)
            {
                for (int idx = 0; idx < height / width; idx++)
                {
                    int yy = y + (idx * blockSize);
                    for (int i = 0; i < width; i++)
                    {
                        row[(idx * width) + i] = io.Get(i, yy);
                    }
                }

                Dct1D(row, 0, height, buf, dir);
                for (int idx = 0; idx < height / width; idx++)
                {
                    int yy = y + (idx * blockSize);
                    for (int i = 0; i < width; i++)
                    {
                        io.Set(i, yy, row[(idx * width) + i]);
                    }
                }
            }
        }

        TransposeBlocks(io, blockSize);
    }

    private static void TransposeBlocks(Grid io, int blockSize)
    {
        for (int by = 0; by < io.H; by += blockSize)
        {
            for (int bx = 0; bx < io.W; bx += blockSize)
            {
                for (int dy = 0; dy < blockSize; dy++)
                {
                    for (int dx = dy + 1; dx < blockSize; dx++)
                    {
                        Swap(io, bx + dx, by + dy, bx + dy, by + dx);
                    }
                }
            }
        }
    }

    // --- special transforms ---
    private static void AuxIdct2InPlace(Grid block, int size)
    {
        int num = size / 2;
        float[] scratch = new float[size * size];
        for (int y = 0; y < num; y++)
        {
            for (int x = 0; x < num; x++)
            {
                float c00 = block.Get(x, y);
                float c01 = block.Get(x + num, y);
                float c10 = block.Get(x, y + num);
                float c11 = block.Get(x + num, y + num);
                scratch[(2 * y * size) + (2 * x)] = c00 + c01 + c10 + c11;
                scratch[(2 * y * size) + (2 * x) + 1] = c00 + c01 - c10 - c11;
                scratch[(((2 * y) + 1) * size) + (2 * x)] = c00 - c01 + c10 - c11;
                scratch[(((2 * y) + 1) * size) + (2 * x) + 1] = c00 - c01 - c10 + c11;
            }
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                block.Set(x, y, scratch[(y * size) + x]);
            }
        }
    }

    private static void TransformDct2(Grid coeff)
    {
        AuxIdct2InPlace(coeff, 2);
        AuxIdct2InPlace(coeff, 4);
        AuxIdct2InPlace(coeff, 8);
    }

    private static void TransformDct4(Grid coeff)
    {
        AuxIdct2InPlace(coeff, 2);
        float[] scratch = new float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                var s = new Grid(scratch, ((y * 2) + x) * 16, 4, 4, 4);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        s.Set(iy, ix, coeff.Get(x + (ix * 2), y + (iy * 2)));
                    }
                }

                Dct2D(s, true);
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int b = ((y * 2) + x) * 16;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        coeff.Set((x * 4) + ix, (y * 4) + iy, scratch[b + (iy * 4) + ix]);
                    }
                }
            }
        }
    }

    private static void TransformHornuss(Grid coeff)
    {
        AuxIdct2InPlace(coeff, 2);
        float[] scratch = new float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int b = ((y * 2) + x) * 16;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        scratch[b + (iy * 4) + ix] = coeff.Get(x + (ix * 2), y + (iy * 2));
                    }
                }

                float residualSum = 0;
                for (int i = 1; i < 16; i++)
                {
                    residualSum += scratch[b + i];
                }

                float avg = scratch[b] - (residualSum / 16f);
                scratch[b] = scratch[b + 5];
                scratch[b + 5] = 0f;
                for (int i = 0; i < 16; i++)
                {
                    scratch[b + i] += avg;
                }
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int b = ((y * 2) + x) * 16;
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        coeff.Set((x * 4) + ix, (y * 4) + iy, scratch[b + (iy * 4) + ix]);
                    }
                }
            }
        }
    }

    private static void TransformDct4x8(Grid coeff, bool tr)
    {
        float c0 = coeff.Get(0, 0);
        float c1 = coeff.Get(0, 1);
        coeff.Set(0, 0, c0 + c1);
        coeff.Set(0, 1, c0 - c1);
        float[] scratch = new float[64];
        for (int idx = 0; idx < 2; idx++)
        {
            var s = new Grid(scratch, idx * 32, 8, 8, 4);
            for (int iy = 0; iy < 4; iy++)
            {
                for (int ix = 0; ix < 8; ix++)
                {
                    s.Set(ix, iy, coeff.Get(ix, (iy * 2) + idx));
                }
            }

            Dct2D(s, true);
        }

        if (tr)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    coeff.Set(y, x, scratch[(y * 8) + x]);
                }
            }
        }
        else
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    coeff.Set(x, y, scratch[(y * 8) + x]);
                }
            }
        }
    }

    private static void TransformAfv(Grid coeff, int n)
    {
        int flipX = n % 2;
        int flipY = n / 2;
        float[] coeffAfv = new float[16];
        coeffAfv[0] = (coeff.Get(0, 0) + coeff.Get(1, 0) + coeff.Get(0, 1)) * 4f;
        for (int idx = 1; idx < 16; idx++)
        {
            int iy = idx / 4, ix = idx % 4;
            coeffAfv[idx] = coeff.Get(2 * ix, 2 * iy);
        }

        float[] samplesAfv = new float[16];
        for (int i = 0; i < 16; i++)
        {
            float[] basis = JxlVarDctTables.AfvBasis[i];
            float cf = coeffAfv[i];
            for (int j = 0; j < 16; j++)
            {
                samplesAfv[j] += cf * basis[j];
            }
        }

        float[] scratch4x4 = new float[16];
        scratch4x4[0] = coeff.Get(0, 0) - coeff.Get(1, 0) + coeff.Get(0, 1);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if ((ix | iy) == 0)
                {
                    continue;
                }

                scratch4x4[(ix * 4) + iy] = coeff.Get((2 * ix) + 1, 2 * iy);
            }
        }

        Dct2D(new Grid(scratch4x4, 0, 4, 4, 4), true);

        float[] scratch4x8 = new float[32];
        scratch4x8[0] = coeff.Get(0, 0) - coeff.Get(0, 1);
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 8; ix++)
            {
                if ((ix | iy) == 0)
                {
                    continue;
                }

                scratch4x8[(iy * 8) + ix] = coeff.Get(ix, (2 * iy) + 1);
            }
        }

        Dct2D(new Grid(scratch4x8, 0, 8, 8, 4), true);

        for (int iy = 0; iy < 4; iy++)
        {
            int afvY = flipY == 0 ? iy : 3 - iy;
            for (int ix = 0; ix < 4; ix++)
            {
                int afvX = flipX == 0 ? ix : 3 - ix;
                coeff.Set((flipX * 4) + ix, (flipY * 4) + iy, samplesAfv[(afvY * 4) + afvX]);
            }
        }

        for (int iy = 0; iy < 4; iy++)
        {
            int y = (flipY * 4) + iy;
            for (int ix = 0; ix < 4; ix++)
            {
                int x = ((1 - flipX) * 4) + ix;
                coeff.Set(x, y, scratch4x4[(iy * 4) + ix]);
            }
        }

        for (int iy = 0; iy < 4; iy++)
        {
            int y = ((1 - flipY) * 4) + iy;
            for (int x = 0; x < 8; x++)
            {
                coeff.Set(x, y, scratch4x8[(iy * 8) + x]);
            }
        }
    }

    private static void Transform(Grid coeff, TransformType t)
    {
        switch (t)
        {
            case TransformType.Dct2: TransformDct2(coeff); break;
            case TransformType.Dct4: TransformDct4(coeff); break;
            case TransformType.Hornuss: TransformHornuss(coeff); break;
            case TransformType.Dct4x8: TransformDct4x8(coeff, false); break;
            case TransformType.Dct8x4: TransformDct4x8(coeff, true); break;
            case TransformType.Afv0: TransformAfv(coeff, 0); break;
            case TransformType.Afv1: TransformAfv(coeff, 1); break;
            case TransformType.Afv2: TransformAfv(coeff, 2); break;
            case TransformType.Afv3: TransformAfv(coeff, 3); break;
            default: Dct2D(coeff, true); break;
        }
    }

    /// <summary>
    /// For a varblock: sets the DC (block 0,0) region from the LF image (forward-DCT of the LF block
    /// for multi-block transforms), then applies the inverse transform to produce spatial pixels.
    /// </summary>
    public static void TransformVarblock(Grid coeff, TransformType dctSelect, float[] lfBuf, int lfStride, int lfX, int lfY)
    {
        var (bw, bh) = DctSelectSize(dctSelect);
        int logbw = System.Numerics.BitOperations.TrailingZeroCount(bw);
        int logbh = System.Numerics.BitOperations.TrailingZeroCount(bh);

        bool single = dctSelect is TransformType.Hornuss or TransformType.Dct2 or TransformType.Dct4
            or TransformType.Dct8x4 or TransformType.Dct4x8 or TransformType.Dct8
            or TransformType.Afv0 or TransformType.Afv1 or TransformType.Afv2 or TransformType.Afv3;

        var lfSub = new Grid(coeff.Buf, coeff.Off, coeff.Stride, bw, bh);
        if (single)
        {
            lfSub.Set(0, 0, lfBuf[(lfY * lfStride) + lfX]);
        }
        else
        {
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    lfSub.Set(x, y, lfBuf[((lfY + y) * lfStride) + lfX + x]);
                }
            }

            Dct2D(lfSub, false);
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    lfSub.Set(x, y, lfSub.Get(x, y) / (ScaleF(y, 5 - logbh) * ScaleF(x, 5 - logbw)));
                }
            }
        }

        var block = new Grid(coeff.Buf, coeff.Off, coeff.Stride, bw * 8, bh * 8);
        Transform(block, dctSelect);
    }
}
