namespace SharpImage.Formats.Av1;

/// <summary>
/// Interintra wedge and blend mask generation.
/// Faithful port of dav1d wedge.c (init_ii_wedge_masks + II_MASK lookup).
/// Generates the same mask bytes dav1d produces at init time.
/// </summary>
public static class Av1WedgeMasks
{
    private enum WedgeDirection : byte
    {
        Horizontal = 0, Vertical = 1, Oblique27 = 2, Oblique63 = 3, Oblique117 = 4, Oblique153 = 5
    }

    private readonly struct WedgeCode
    {
        public readonly WedgeDirection Dir;
        public readonly int XOff, YOff;
        public WedgeCode(WedgeDirection d, int x, int y) { Dir = d; XOff = x; YOff = y; }
    }

    // wedge_codebook_16_hgtw / hltw / heqw (dav1d wedge.c:53-84)
    private static readonly WedgeCode[] CodeHgtw =
    {
        new(WedgeDirection.Oblique27, 4, 4), new(WedgeDirection.Oblique63, 4, 4),
        new(WedgeDirection.Oblique117, 4, 4), new(WedgeDirection.Oblique153, 4, 4),
        new(WedgeDirection.Horizontal, 4, 2), new(WedgeDirection.Horizontal, 4, 4),
        new(WedgeDirection.Horizontal, 4, 6), new(WedgeDirection.Vertical, 4, 4),
        new(WedgeDirection.Oblique27, 4, 2), new(WedgeDirection.Oblique27, 4, 6),
        new(WedgeDirection.Oblique153, 4, 2), new(WedgeDirection.Oblique153, 4, 6),
        new(WedgeDirection.Oblique63, 2, 4), new(WedgeDirection.Oblique63, 6, 4),
        new(WedgeDirection.Oblique117, 2, 4), new(WedgeDirection.Oblique117, 6, 4),
    };
    private static readonly WedgeCode[] CodeHltw =
    {
        new(WedgeDirection.Oblique27, 4, 4), new(WedgeDirection.Oblique63, 4, 4),
        new(WedgeDirection.Oblique117, 4, 4), new(WedgeDirection.Oblique153, 4, 4),
        new(WedgeDirection.Vertical, 2, 4), new(WedgeDirection.Vertical, 4, 4),
        new(WedgeDirection.Vertical, 6, 4), new(WedgeDirection.Horizontal, 4, 4),
        new(WedgeDirection.Oblique27, 4, 2), new(WedgeDirection.Oblique27, 4, 6),
        new(WedgeDirection.Oblique153, 4, 2), new(WedgeDirection.Oblique153, 4, 6),
        new(WedgeDirection.Oblique63, 2, 4), new(WedgeDirection.Oblique63, 6, 4),
        new(WedgeDirection.Oblique117, 2, 4), new(WedgeDirection.Oblique117, 6, 4),
    };
    private static readonly WedgeCode[] CodeHeqw =
    {
        new(WedgeDirection.Oblique27, 4, 4), new(WedgeDirection.Oblique63, 4, 4),
        new(WedgeDirection.Oblique117, 4, 4), new(WedgeDirection.Oblique153, 4, 4),
        new(WedgeDirection.Horizontal, 4, 2), new(WedgeDirection.Horizontal, 4, 6),
        new(WedgeDirection.Vertical, 2, 4), new(WedgeDirection.Vertical, 6, 4),
        new(WedgeDirection.Oblique27, 4, 2), new(WedgeDirection.Oblique27, 4, 6),
        new(WedgeDirection.Oblique153, 4, 2), new(WedgeDirection.Oblique153, 4, 6),
        new(WedgeDirection.Oblique63, 2, 4), new(WedgeDirection.Oblique63, 6, 4),
        new(WedgeDirection.Oblique117, 2, 4), new(WedgeDirection.Oblique117, 6, 4),
    };

    // wedge_master_border (dav1d wedge.c:216-220)
    private static readonly byte[] BorderOdd = { 1, 2, 6, 18, 37, 53, 60, 63 };
    private static readonly byte[] BorderEven = { 1, 4, 11, 27, 46, 58, 62, 63 };
    private static readonly byte[] BorderVert = { 0, 2, 7, 21, 43, 57, 62, 64 };

    // ii_weights_1d (dav1d wedge.c:191-194)
    private static readonly byte[] IiWeights1D =
    {
        60, 52, 45, 39, 34, 30, 26, 22, 19, 17, 15, 13, 11, 10, 8, 7,
        6, 6, 5, 4, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1,
    };

    // The 9 interintra block sizes (dav1d fill() calls).
    private readonly struct SizeInfo
    {
        public readonly int Bs, Bw4, Bh4;
        public readonly WedgeCode[] Cb;
        public readonly uint Signs;
        public SizeInfo(int bs, int bw4, int bh4, WedgeCode[] cb, uint signs)
        { Bs = bs; Bw4 = bw4; Bh4 = bh4; Cb = cb; Signs = signs; }
    }

    private static readonly SizeInfo[] Sizes =
    {
        new SizeInfo(7, 8, 8, CodeHeqw, 0x7bfb),   // Bs32x32
        new SizeInfo(8, 8, 4, CodeHltw, 0x7beb),   // Bs32x16
        new SizeInfo(9, 8, 2, CodeHltw, 0x6beb),   // Bs32x8
        new SizeInfo(11, 4, 8, CodeHgtw, 0x7beb),  // Bs16x32
        new SizeInfo(12, 4, 4, CodeHeqw, 0x7bfb),  // Bs16x16
        new SizeInfo(13, 4, 2, CodeHltw, 0x7beb),  // Bs16x8
        new SizeInfo(15, 2, 8, CodeHgtw, 0x7aeb),  // Bs8x32
        new SizeInfo(16, 2, 4, CodeHgtw, 0x7beb),  // Bs8x16
        new SizeInfo(17, 2, 2, CodeHeqw, 0x7bfb),  // Bs8x8
    };

    // ii (BLEND) parent arrays — dav1d BUILD_NONDC_II_MASKS dims (w,h,step).
    private readonly struct IiParent
    {
        public readonly int W, H;
        public readonly byte[] Masks; // 3 masks (v,h,sm) of W*H each
        public IiParent(int w, int h, int step)
        {
            W = w; H = h;
            Masks = new byte[3 * w * h];
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                byte wv = IiWeights1D[y * step];
                for (int x = 0; x < w; x++)
                {
                    Masks[off + x] = wv;
                    Masks[w * h + off + x] = IiWeights1D[x * step];
                    Masks[2 * w * h + off + x] = IiWeights1D[Math.Min(x, y) * step];
                }
            }
        }
    }

    private static readonly IiParent[] IiParents =
    {
        new(32, 32, 1), new(16, 32, 1), new(16, 16, 2),
        new(8, 32, 1), new(8, 16, 2), new(8, 8, 4),
        new(4, 16, 2), new(4, 8, 4), new(4, 4, 8),
    };

    // Find the parent for a mask of pixel dims (mw, mh): prefer exact width with
    // smallest height >= mh (dav1d reads rows from the parent at that width).
    private static IiParent FindIiParent(int mw, int mh)
    {
        IiParent best = default;
        foreach (var p in IiParents)
        {
            if (p.W != mw) continue;
            if (best.Masks == null || (p.H >= mh && p.H < best.H)) best = p;
        }
        if (best.Masks != null) return best;
        // No exact-width parent (e.g. 4x32) — use the widest matching height at width 4
        IiParent fallback = IiParents[^1];
        foreach (var p in IiParents)
            if (p.W == mw && p.H <= mh) fallback = p;
        return fallback;
    }

    private static readonly byte[][][] Wedge444; // [sizeIdx][idx] -> bw4*4 x bh4*4 bytes
    private static readonly byte[][][] Wedge422; // [sizeIdx][idx] -> bw4*2 x bh4*4 bytes
    private static readonly byte[][][] Wedge420; // [sizeIdx][idx] -> bw4*2 x bh4*2 bytes
    private static readonly byte[] IiDc;         // 32x32 all-32 shared blend mask

    static Av1WedgeMasks()
    {
        // Master templates (dav1d wedge.c:223-238)
        var master = new byte[6][];
        for (int d = 0; d < 6; d++) master[d] = new byte[64 * 64];

        for (int y = 0; y < 64; y++)
            InsertBorder(master[(int)WedgeDirection.Vertical].AsSpan(y * 64, 64), BorderVert, 32);
        for (int y = 0, ctr = 48; y < 64; y += 2, ctr--)
        {
            InsertBorder(master[(int)WedgeDirection.Oblique63].AsSpan(y * 64, 64), BorderEven, ctr);
            InsertBorder(master[(int)WedgeDirection.Oblique63].AsSpan((y + 1) * 64, 64), BorderOdd, ctr - 1);
        }
        Transpose(master[(int)WedgeDirection.Oblique27], master[(int)WedgeDirection.Oblique63]);
        Transpose(master[(int)WedgeDirection.Horizontal], master[(int)WedgeDirection.Vertical]);
        HFlip(master[(int)WedgeDirection.Oblique117], master[(int)WedgeDirection.Oblique63]);
        HFlip(master[(int)WedgeDirection.Oblique153], master[(int)WedgeDirection.Oblique27]);

        int n = Sizes.Length;
        Wedge444 = new byte[n][][];
        Wedge422 = new byte[n][][];
        Wedge420 = new byte[n][][];

        for (int s = 0; s < n; s++)
        {
            var si = Sizes[s];
            int w = si.Bw4 * 4, h = si.Bh4 * 4; // luma (444) pixel dims
            Wedge444[s] = new byte[16][];
            Wedge422[s] = new byte[16][];
            Wedge420[s] = new byte[16][];

            uint signs = si.Signs;
            for (int idx = 0; idx < 16; idx++)
            {
                int sign = (int)(signs & 1);
                signs >>= 1;
                var code = si.Cb[idx];
                int xOff = 32 - (w * code.XOff >> 3);
                int yOff = 32 - (h * code.YOff >> 3);

                Wedge444[s][idx] = Copy2D(master[(int)code.Dir], sign, w, h, xOff, yOff);
                Wedge422[s][idx] = InitChroma(Wedge444[s][idx], sign, w, h, 0);
                Wedge420[s][idx] = InitChroma(Wedge444[s][idx], sign, w, h, 1);
            }
        }

        IiDc = new byte[32 * 32];
        IiDc.AsSpan().Fill(32);
    }

    private static void InsertBorder(Span<byte> dst, byte[] src, int ctr)
    {
        dst.Clear();
        int dstOff = Math.Max(ctr, 4) - 4;
        int srcOff = Math.Max(4 - ctr, 0);
        int len = Math.Min(64 - ctr, 8);
        for (int i = 0; i < len; i++) dst[dstOff + i] = src[srcOff + i];
        if (ctr < 64 - 4)
            for (int i = ctr + 4; i < 64; i++) dst[i] = 64;
    }

    private static void Transpose(byte[] dst, byte[] src)
    {
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                dst[x * 64 + y] = src[y * 64 + x];
    }

    private static void HFlip(byte[] dst, byte[] src)
    {
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                dst[y * 64 + 64 - 1 - x] = src[y * 64 + x];
    }

    // copy2d (dav1d wedge.c:109-127): extract w x h mask from 64x64 master at (xOff, yOff)
    private static byte[] Copy2D(byte[] master, int sign, int w, int h, int xOff, int yOff)
    {
        var dst = new byte[w * h];
        int src = yOff * 64 + xOff;
        if (sign != 0)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                    dst[y * w + x] = (byte)(64 - master[src + x]);
                src += 64;
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                Array.Copy(master, src, dst, y * w, w);
                src += 64;
            }
        }
        return dst;
    }

    // init_chroma (dav1d wedge.c:131-146): downsample luma mask to chroma
    private static byte[] InitChroma(byte[] luma, int sign, int w, int h, int ssVer)
    {
        int cw = w >> 1, ch = h >> ssVer;
        var chroma = new byte[cw * ch];
        int lumaOff = 0, chromaOff = 0;
        for (int y = 0; y < h; y += 1 + ssVer)
        {
            for (int x = 0; x < w; x += 2)
            {
                int sum = luma[lumaOff + x] + luma[lumaOff + x + 1] + 1;
                if (ssVer != 0) sum += luma[lumaOff + w + x] + luma[lumaOff + w + x + 1] + 1;
                chroma[chromaOff + (x >> 1)] = (byte)((sum - sign) >> (1 + ssVer));
            }
            lumaOff += w << ssVer;
            chromaOff += cw;
        }
        return chroma;
    }

    // Map our Av1BlockSize enum value to the size index in Sizes, or -1 if not interintra-sized.
    private static int SizeIndex(int bs)
    {
        for (int i = 0; i < Sizes.Length; i++)
            if (Sizes[i].Bs == bs) return i;
        return -1;
    }

    /// <summary>
    /// Get the interintra mask for a block (dav1d II_MASK).
    /// c: 0 = luma (444), 1 = chroma 422, 2 = chroma 420.
    /// type: 1 = BLEND, 2 = WEDGE.
    /// modeOrIdx: interintra_mode for BLEND (0=DC,1=V,2=H,3=Smooth), wedge_idx for WEDGE.
    /// w/h are set to the mask pixel dims.
    /// </summary>
    public static ReadOnlySpan<byte> GetMask(int c, int bs, int type, int modeOrIdx, out int w, out int h)
    {
        int s = SizeIndex(bs);
        if (s < 0) { w = h = 0; return ReadOnlySpan<byte>.Empty; }
        var si = Sizes[s];

        if (type == 2) // WEDGE
        {
            if (c == 0) { w = si.Bw4 * 4; h = si.Bh4 * 4; return Wedge444[s][modeOrIdx]; }
            if (c == 1) { w = si.Bw4 * 2; h = si.Bh4 * 4; return Wedge422[s][modeOrIdx]; }
            w = si.Bw4 * 2; h = si.Bh4 * 2; return Wedge420[s][modeOrIdx];
        }

        // BLEND
        if (modeOrIdx == 0)
        {
            // DC pred: shared all-32 mask (block reads its own dims from the 32x32 array)
            w = c == 0 ? si.Bw4 * 4 : si.Bw4 * 2;
            h = c == 2 ? si.Bh4 * 2 : si.Bh4 * 4;
            return IiDc.AsSpan(0, w * h);
        }

        int p = modeOrIdx - 1; // 0=v, 1=h, 2=sm
        if (c == 0)
        {
            w = si.Bw4 * 4; h = si.Bh4 * 4;
            var par = FindIiParent(w, h);
            return par.Masks.AsSpan(p * par.W * par.H, h * w);
        }
        if (c == 1)
        {
            w = si.Bw4 * 2; h = si.Bh4 * 4;
            var par = FindIiParent(w, h);
            return par.Masks.AsSpan(p * par.W * par.H, h * w);
        }
        w = si.Bw4 * 2; h = si.Bh4 * 2;
        var par420 = FindIiParent(w, h);
        return par420.Masks.AsSpan(p * par420.W * par420.H, h * w);
    }
}
