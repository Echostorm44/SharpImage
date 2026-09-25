namespace SharpImage.Formats;

// libjpeg-turbo 3.1's colour quantizers (djpeg -colors / -dither / -onepass / -map): jquant1.c's one-pass quantizer
// (an equally spaced colormap with no, ordered or Floyd-Steinberg dithering) and jquant2.c's two-pass quantizer (a
// median-cut colormap from a 5-6-5 histogram, or a given colormap, with no or Floyd-Steinberg dithering), sample for
// sample. Samples are at the file's precision (max = 255 or 4095); an output row's codes index Colormap.
public static partial class JpegCoder
{
    private sealed class LjQuant1
    {
        private static readonly byte[,] BaseDither =
        {
            { 0, 192, 48, 240, 12, 204, 60, 252, 3, 195, 51, 243, 15, 207, 63, 255 },
            { 128, 64, 176, 112, 140, 76, 188, 124, 131, 67, 179, 115, 143, 79, 191, 127 },
            { 32, 224, 16, 208, 44, 236, 28, 220, 35, 227, 19, 211, 47, 239, 31, 223 },
            { 160, 96, 144, 80, 172, 108, 156, 92, 163, 99, 147, 83, 175, 111, 159, 95 },
            { 8, 200, 56, 248, 4, 196, 52, 244, 11, 203, 59, 251, 7, 199, 55, 247 },
            { 136, 72, 184, 120, 132, 68, 180, 116, 139, 75, 187, 123, 135, 71, 183, 119 },
            { 40, 232, 24, 216, 36, 228, 20, 212, 43, 235, 27, 219, 39, 231, 23, 215 },
            { 168, 104, 152, 88, 164, 100, 148, 84, 171, 107, 155, 91, 167, 103, 151, 87 },
            { 2, 194, 50, 242, 14, 206, 62, 254, 1, 193, 49, 241, 13, 205, 61, 253 },
            { 130, 66, 178, 114, 142, 78, 190, 126, 129, 65, 177, 113, 141, 77, 189, 125 },
            { 34, 226, 18, 210, 46, 238, 30, 222, 33, 225, 17, 209, 45, 237, 29, 221 },
            { 162, 98, 146, 82, 174, 110, 158, 94, 161, 97, 145, 81, 173, 109, 157, 93 },
            { 10, 202, 58, 250, 6, 198, 54, 246, 9, 201, 57, 249, 5, 197, 53, 245 },
            { 138, 74, 186, 122, 134, 70, 182, 118, 137, 73, 185, 121, 133, 69, 181, 117 },
            { 42, 234, 26, 218, 38, 230, 22, 214, 41, 233, 25, 217, 37, 229, 21, 213 },
            { 170, 106, 154, 90, 166, 102, 150, 86, 169, 105, 153, 89, 165, 101, 149, 85 },
        };

        private readonly int nc, max, width;
        private readonly JpegDitherMode dither;
        public readonly int[][] Colormap;
        public readonly int Count;
        private readonly int[] ncolors = new int[4];
        private readonly int[][] colorindex;
        private readonly int ciOffset;              // colorindex[ci][ciOffset + value] (padded for ordered dither)
        private readonly int[][,] odither = new int[4][,];
        private readonly int[][] fserrors = new int[4][];
        private int rowIndex;
        private bool oddRow;

        public LjQuant1(int nc, int max, int desired, JpegDitherMode dither, int width, bool rgb)
        {
            this.nc = nc;
            this.max = max;
            this.width = width;
            this.dither = dither;
            if (desired > max + 1) throw new ArgumentException($"Cannot quantize to more than {max + 1} colors");

            // select_ncolors: the largest equal count per component, then one more for G, R, B (or 0, 1, ...) while it fits
            int iroot = 1;
            long temp;
            do
            {
                iroot++;
                temp = iroot;
                for (int i = 1; i < nc; i++) temp *= iroot;
            } while (temp <= desired);
            iroot--;
            if (iroot < 2) throw new ArgumentException($"Cannot quantize to fewer than {temp} colors");
            int total = 1;
            for (int i = 0; i < nc; i++) { ncolors[i] = iroot; total *= iroot; }
            ReadOnlySpan<int> rgbOrder = [1, 0, 2];
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < nc; i++)
                {
                    int j = rgb ? rgbOrder[i] : i;
                    temp = total / ncolors[j];
                    temp *= ncolors[j] + 1;
                    if (temp > desired) break;
                    ncolors[j]++;
                    total = (int)temp;
                    changed = true;
                }
            } while (changed);

            // create_colormap
            Count = total;
            Colormap = new int[nc][];
            int blkdist = total;
            for (int i = 0; i < nc; i++)
            {
                Colormap[i] = new int[total];
                int nci = ncolors[i], blksize = blkdist / nci;
                for (int j = 0; j < nci; j++)
                {
                    int val = (int)(((long)j * max + (nci - 1) / 2) / (nci - 1));
                    for (int ptr = j * blksize; ptr < total; ptr += blkdist)
                        for (int k = 0; k < blksize; k++) Colormap[i][ptr + k] = val;
                }
                blkdist = blksize;
            }

            // create_colorindex (padded by 2 * MAXJSAMPLE for ordered dithering)
            int pad = dither == JpegDitherMode.Ordered ? max * 2 : 0;
            ciOffset = pad != 0 ? max : 0;
            colorindex = new int[nc][];
            int bs = total;
            for (int i = 0; i < nc; i++)
            {
                int nci = ncolors[i];
                bs /= nci;
                var idx = colorindex[i] = new int[max + 1 + pad];
                int val = 0, k = Largest(0, nci - 1);
                for (int j = 0; j <= max; j++)
                {
                    while (j > k) k = Largest(++val, nci - 1);
                    idx[ciOffset + j] = val * bs;
                }
                if (pad != 0)
                    for (int j = 1; j <= max; j++)
                    {
                        idx[ciOffset - j] = idx[ciOffset];
                        idx[ciOffset + max + j] = idx[ciOffset + max];
                    }
            }

            if (dither == JpegDitherMode.Ordered)
            {
                // create_odither_tables: one matrix per distinct component count
                for (int i = 0; i < nc; i++)
                {
                    int nci = ncolors[i];
                    for (int j = 0; j < i; j++)
                        if (nci == ncolors[j]) { odither[i] = odither[j]; break; }
                    if (odither[i] != null) continue;
                    var m = odither[i] = new int[16, 16];
                    int den = 2 * 256 * (nci - 1);
                    for (int j = 0; j < 16; j++)
                        for (int k = 0; k < 16; k++)
                        {
                            int num = (255 - 2 * BaseDither[j, k]) * max;
                            m[j, k] = num < 0 ? -(-num / den) : num / den;
                        }
                }
            }
            if (dither == JpegDitherMode.FloydSteinberg)
                for (int i = 0; i < nc; i++) fserrors[i] = new int[width + 2];
        }

        private int Largest(int j, int maxj) => (int)(((long)(2 * j + 1) * max + maxj) / (2 * maxj));

        // FSERROR is INT16 at 8 bits
        private int Err(int v) => max == 255 ? (short)v : v;

        public void QuantizeRow(int[] input, int[] codes)
        {
            Array.Clear(codes, 0, width);
            switch (dither)
            {
                case JpegDitherMode.None:
                    for (int x = 0; x < width; x++)
                    {
                        int code = 0;
                        for (int ci = 0; ci < nc; ci++) code += colorindex[ci][input[x * nc + ci]];
                        codes[x] = code;
                    }
                    break;
                case JpegDitherMode.Ordered:
                    for (int ci = 0; ci < nc; ci++)
                    {
                        var idx = colorindex[ci];
                        var m = odither[ci];
                        for (int x = 0; x < width; x++) codes[x] += idx[ciOffset + input[x * nc + ci] + m[rowIndex, x & 15]];
                    }
                    rowIndex = (rowIndex + 1) & 15;
                    break;
                default:
                    for (int ci = 0; ci < nc; ci++)
                    {
                        var err = fserrors[ci];
                        var idx = colorindex[ci];
                        var cm = Colormap[ci];
                        int dir, pos, outPos, e;
                        if (oddRow) { pos = (width - 1) * nc + ci; outPos = width - 1; dir = -1; e = width + 1; }
                        else { pos = ci; outPos = 0; dir = 1; e = 0; }
                        int cur = 0, belowerr = 0, bpreverr = 0;
                        for (int col = width; col > 0; col--)
                        {
                            cur = (cur + err[e + dir] + 8) >> 4;
                            cur += input[pos];
                            cur = Math.Clamp(cur, 0, max);
                            int pixcode = idx[cur];
                            codes[outPos] += pixcode;
                            cur -= cm[pixcode];
                            int bnexterr = cur, delta = cur * 2;
                            cur += delta;
                            err[e] = Err(bpreverr + cur);
                            cur += delta;
                            bpreverr = belowerr + cur;
                            belowerr = bnexterr;
                            cur += delta;
                            pos += dir * nc;
                            outPos += dir;
                            e += dir;
                        }
                        err[e] = Err(bpreverr);
                    }
                    oddRow = !oddRow;
                    break;
            }
        }
    }

    private sealed class LjQuant2
    {
        private const int C0Elems = 32, C1Elems = 64, C2Elems = 32;
        private const int C0Scale = 2, C1Scale = 3, C2Scale = 1;   // R, G, B (JCS_RGB)
        private readonly int max, width, sh0, sh1, sh2;
        private readonly ushort[] hist = new ushort[C0Elems * C1Elems * C2Elems];
        private readonly bool fs;
        public int[][] Colormap = [];
        public int Count;
        private int[]? fserrors;
        private int[]? errorLimit;   // indexed by value + max
        private bool oddRow;

        private struct Box
        {
            public int C0Min, C0Max, C1Min, C1Max, C2Min, C2Max, Volume;
            public long ColorCount;
        }

        public LjQuant2(int max, int width, bool dither)
        {
            this.max = max;
            this.width = width;
            int bits = max == 255 ? 8 : 12;
            sh0 = bits - 5; sh1 = bits - 6; sh2 = bits - 5;
            fs = dither;
        }

        private static int H(int c0, int c1, int c2) => (c0 * C1Elems + c1) * C2Elems + c2;

        // prescan_quantize: a saturating 16-bit count per 5-6-5 cell
        public void Prescan(int[] row)
        {
            for (int x = 0; x < width; x++)
            {
                int h = H(row[x * 3] >> sh0, row[x * 3 + 1] >> sh1, row[x * 3 + 2] >> sh2);
                if (hist[h] != ushort.MaxValue) hist[h]++;
            }
        }

        /// <summary>finish_pass1 / select_colors: the median-cut colormap.</summary>
        public void SelectColors(int desired)
        {
            if (desired < 8) throw new ArgumentException("Cannot quantize to fewer than 8 colors");
            if (desired > max + 1) throw new ArgumentException($"Cannot quantize to more than {max + 1} colors");
            var boxes = new Box[desired];
            boxes[0] = new Box { C0Max = max >> sh0, C1Max = max >> sh1, C2Max = max >> sh2 };
            UpdateBox(ref boxes[0]);
            int n = MedianCut(boxes, 1, desired);
            Colormap = [new int[n], new int[n], new int[n]];
            for (int i = 0; i < n; i++) ComputeColor(boxes[i], i);
            Count = n;
            Array.Clear(hist);
        }

        /// <summary>A given colormap (djpeg -map): pass 2 only.</summary>
        public void UseColormap(int[][] cm, int count)
        {
            if (count < 1) throw new ArgumentException("Cannot quantize to fewer than 1 colors");
            if (count > max + 1) throw new ArgumentException($"Cannot quantize to more than {max + 1} colors");
            Colormap = cm;
            Count = count;
            Array.Clear(hist);
        }

        private void UpdateBox(ref Box b)
        {
            int c0min = b.C0Min, c0max = b.C0Max, c1min = b.C1Min, c1max = b.C1Max, c2min = b.C2Min, c2max = b.C2Max;
            bool Any(int c0a, int c0b, int c1a, int c1b, int c2a, int c2b)
            {
                for (int c0 = c0a; c0 <= c0b; c0++)
                    for (int c1 = c1a; c1 <= c1b; c1++)
                        for (int c2 = c2a; c2 <= c2b; c2++)
                            if (hist[H(c0, c1, c2)] != 0) return true;
                return false;
            }
            if (c0max > c0min)
                for (int c0 = c0min; c0 <= c0max; c0++)
                    if (Any(c0, c0, c1min, c1max, c2min, c2max)) { b.C0Min = c0min = c0; break; }
            if (c0max > c0min)
                for (int c0 = c0max; c0 >= c0min; c0--)
                    if (Any(c0, c0, c1min, c1max, c2min, c2max)) { b.C0Max = c0max = c0; break; }
            if (c1max > c1min)
                for (int c1 = c1min; c1 <= c1max; c1++)
                    if (Any(c0min, c0max, c1, c1, c2min, c2max)) { b.C1Min = c1min = c1; break; }
            if (c1max > c1min)
                for (int c1 = c1max; c1 >= c1min; c1--)
                    if (Any(c0min, c0max, c1, c1, c2min, c2max)) { b.C1Max = c1max = c1; break; }
            if (c2max > c2min)
                for (int c2 = c2min; c2 <= c2max; c2++)
                    if (Any(c0min, c0max, c1min, c1max, c2, c2)) { b.C2Min = c2min = c2; break; }
            if (c2max > c2min)
                for (int c2 = c2max; c2 >= c2min; c2--)
                    if (Any(c0min, c0max, c1min, c1max, c2, c2)) { b.C2Max = c2max = c2; break; }
            int dist0 = ((c0max - c0min) << sh0) * C0Scale;
            int dist1 = ((c1max - c1min) << sh1) * C1Scale;
            int dist2 = ((c2max - c2min) << sh2) * C2Scale;
            b.Volume = dist0 * dist0 + dist1 * dist1 + dist2 * dist2;
            long ccount = 0;
            for (int c0 = c0min; c0 <= c0max; c0++)
                for (int c1 = c1min; c1 <= c1max; c1++)
                    for (int c2 = c2min; c2 <= c2max; c2++)
                        if (hist[H(c0, c1, c2)] != 0) ccount++;
            b.ColorCount = ccount;
        }

        private int MedianCut(Box[] boxes, int numboxes, int desired)
        {
            while (numboxes < desired)
            {
                int b1 = -1;
                if (numboxes * 2 <= desired)
                {
                    long maxc = 0;
                    for (int i = 0; i < numboxes; i++)
                        if (boxes[i].ColorCount > maxc && boxes[i].Volume > 0) { b1 = i; maxc = boxes[i].ColorCount; }
                }
                else
                {
                    int maxv = 0;
                    for (int i = 0; i < numboxes; i++)
                        if (boxes[i].Volume > maxv) { b1 = i; maxv = boxes[i].Volume; }
                }
                if (b1 < 0) break;
                ref var a = ref boxes[b1];
                ref var nb = ref boxes[numboxes];
                nb = a;
                int c0 = ((a.C0Max - a.C0Min) << sh0) * C0Scale;
                int c1 = ((a.C1Max - a.C1Min) << sh1) * C1Scale;
                int c2 = ((a.C2Max - a.C2Min) << sh2) * C2Scale;
                // rgb_red == 0: G first, then R, then B
                int cmax = c1, n = 1;
                if (c0 > cmax) { cmax = c0; n = 0; }
                if (c2 > cmax) n = 2;
                switch (n)
                {
                    case 0: { int lb = (a.C0Max + a.C0Min) / 2; a.C0Max = lb; nb.C0Min = lb + 1; break; }
                    case 1: { int lb = (a.C1Max + a.C1Min) / 2; a.C1Max = lb; nb.C1Min = lb + 1; break; }
                    default: { int lb = (a.C2Max + a.C2Min) / 2; a.C2Max = lb; nb.C2Min = lb + 1; break; }
                }
                UpdateBox(ref a);
                UpdateBox(ref nb);
                numboxes++;
            }
            return numboxes;
        }

        private void ComputeColor(Box b, int icolor)
        {
            long total = 0, c0total = 0, c1total = 0, c2total = 0;
            for (int c0 = b.C0Min; c0 <= b.C0Max; c0++)
                for (int c1 = b.C1Min; c1 <= b.C1Max; c1++)
                    for (int c2 = b.C2Min; c2 <= b.C2Max; c2++)
                    {
                        long count = hist[H(c0, c1, c2)];
                        if (count == 0) continue;
                        total += count;
                        c0total += ((c0 << sh0) + ((1 << sh0) >> 1)) * count;
                        c1total += ((c1 << sh1) + ((1 << sh1) >> 1)) * count;
                        c2total += ((c2 << sh2) + ((1 << sh2) >> 1)) * count;
                    }
            Colormap[0][icolor] = (int)((c0total + (total >> 1)) / total);
            Colormap[1][icolor] = (int)((c1total + (total >> 1)) / total);
            Colormap[2][icolor] = (int)((c2total + (total >> 1)) / total);
        }

        // find_nearby_colors: the colors that can be nearest to some point of the update box
        private int FindNearbyColors(int minc0, int minc1, int minc2, int[] colorlist)
        {
            int maxc0 = minc0 + ((1 << (sh0 + 2)) - (1 << sh0)), centerc0 = (minc0 + maxc0) >> 1;
            int maxc1 = minc1 + ((1 << (sh1 + 3)) - (1 << sh1)), centerc1 = (minc1 + maxc1) >> 1;
            int maxc2 = minc2 + ((1 << (sh2 + 2)) - (1 << sh2)), centerc2 = (minc2 + maxc2) >> 1;
            int minmaxdist = 0x7FFFFFFF;
            var mindist = new int[Count];
            for (int i = 0; i < Count; i++)
            {
                int minD = 0, maxD = 0;
                void Axis(int x, int minc, int maxc, int centerc, int scale, bool first)
                {
                    int t;
                    if (x < minc)
                    {
                        t = (x - minc) * scale; minD += t * t;
                        t = (x - maxc) * scale; maxD += t * t;
                    }
                    else if (x > maxc)
                    {
                        t = (x - maxc) * scale; minD += t * t;
                        t = (x - minc) * scale; maxD += t * t;
                    }
                    else
                    {
                        t = x <= centerc ? (x - maxc) * scale : (x - minc) * scale;
                        maxD += t * t;
                    }
                }
                Axis(Colormap[0][i], minc0, maxc0, centerc0, C0Scale, true);
                Axis(Colormap[1][i], minc1, maxc1, centerc1, C1Scale, false);
                Axis(Colormap[2][i], minc2, maxc2, centerc2, C2Scale, false);
                mindist[i] = minD;
                if (maxD < minmaxdist) minmaxdist = maxD;
            }
            int ncolors = 0;
            for (int i = 0; i < Count; i++)
                if (mindist[i] <= minmaxdist) colorlist[ncolors++] = i;
            return ncolors;
        }

        // fill_inverse_cmap + find_best_colors: the nearest color for each cell of a 4x8x4 update box
        private void FillInverseCmap(int c0, int c1, int c2)
        {
            c0 >>= 2; c1 >>= 3; c2 >>= 2;
            int minc0 = (c0 << (sh0 + 2)) + ((1 << sh0) >> 1);
            int minc1 = (c1 << (sh1 + 3)) + ((1 << sh1) >> 1);
            int minc2 = (c2 << (sh2 + 2)) + ((1 << sh2) >> 1);
            var colorlist = new int[Count];
            int numcolors = FindNearbyColors(minc0, minc1, minc2, colorlist);
            const int n0 = 4, n1 = 8, n2 = 4;
            Span<int> bestdist = stackalloc int[n0 * n1 * n2];
            Span<int> bestcolor = stackalloc int[n0 * n1 * n2];
            bestdist.Fill(0x7FFFFFFF);
            int step0 = (1 << sh0) * C0Scale, step1 = (1 << sh1) * C1Scale, step2 = (1 << sh2) * C2Scale;
            for (int i = 0; i < numcolors; i++)
            {
                int icolor = colorlist[i];
                int inc0 = (minc0 - Colormap[0][icolor]) * C0Scale;
                int dist0 = inc0 * inc0;
                int inc1 = (minc1 - Colormap[1][icolor]) * C1Scale;
                dist0 += inc1 * inc1;
                int inc2 = (minc2 - Colormap[2][icolor]) * C2Scale;
                dist0 += inc2 * inc2;
                inc0 = inc0 * (2 * step0) + step0 * step0;
                inc1 = inc1 * (2 * step1) + step1 * step1;
                inc2 = inc2 * (2 * step2) + step2 * step2;
                int p = 0;
                int xx0 = inc0;
                for (int ic0 = n0 - 1; ic0 >= 0; ic0--)
                {
                    int dist1 = dist0, xx1 = inc1;
                    for (int ic1 = n1 - 1; ic1 >= 0; ic1--)
                    {
                        int dist2 = dist1, xx2 = inc2;
                        for (int ic2 = n2 - 1; ic2 >= 0; ic2--)
                        {
                            if (dist2 < bestdist[p]) { bestdist[p] = dist2; bestcolor[p] = icolor; }
                            dist2 += xx2;
                            xx2 += 2 * step2 * step2;
                            p++;
                        }
                        dist1 += xx1;
                        xx1 += 2 * step1 * step1;
                    }
                    dist0 += xx0;
                    xx0 += 2 * step0 * step0;
                }
            }
            c0 <<= 2; c1 <<= 3; c2 <<= 2;
            int q = 0;
            for (int ic0 = 0; ic0 < n0; ic0++)
                for (int ic1 = 0; ic1 < n1; ic1++)
                    for (int ic2 = 0; ic2 < n2; ic2++)
                        hist[H(c0 + ic0, c1 + ic1, c2 + ic2)] = (ushort)(bestcolor[q++] + 1);
        }

        private int Lookup(int v0, int v1, int v2)
        {
            int c0 = v0 >> sh0, c1 = v1 >> sh1, c2 = v2 >> sh2;
            int h = H(c0, c1, c2);
            if (hist[h] == 0) FillInverseCmap(c0, c1, c2);
            return hist[h] - 1;
        }

        public void QuantizeRow(int[] input, int[] codes)
        {
            if (!fs)
            {
                for (int x = 0; x < width; x++) codes[x] = Lookup(input[x * 3], input[x * 3 + 1], input[x * 3 + 2]);
                return;
            }
            if (fserrors == null)
            {
                fserrors = new int[(width + 2) * 3];
                // init_error_limit: errors pass through below MAXJSAMPLE/16, grow at half rate to 3/16, then stop
                errorLimit = new int[max * 2 + 1];
                int step = (max + 1) / 16, o = 0, n = 0;
                for (; n < step; n++, o++) { errorLimit[max + n] = o; errorLimit[max - n] = -o; }
                for (; n < step * 3; n++, o += (n & 1) != 0 ? 0 : 1) { errorLimit[max + n] = o; errorLimit[max - n] = -o; }
                for (; n <= max; n++) { errorLimit[max + n] = o; errorLimit[max - n] = -o; }
            }
            var err = fserrors;
            var lim = errorLimit!;
            int dir, dir3, pos, outPos, e;
            if (oddRow) { pos = (width - 1) * 3; outPos = width - 1; dir = -1; dir3 = -3; e = (width + 1) * 3; oddRow = false; }
            else { pos = 0; outPos = 0; dir = 1; dir3 = 3; e = 0; oddRow = true; }
            int cur0 = 0, cur1 = 0, cur2 = 0, below0 = 0, below1 = 0, below2 = 0, bprev0 = 0, bprev1 = 0, bprev2 = 0;
            int Lim(int v) => lim[Math.Clamp(v, -max, max) + max];
            int F(int v) => max == 255 ? (short)v : v;
            for (int col = width; col > 0; col--)
            {
                cur0 = (cur0 + err[e + dir3] + 8) >> 4;
                cur1 = (cur1 + err[e + dir3 + 1] + 8) >> 4;
                cur2 = (cur2 + err[e + dir3 + 2] + 8) >> 4;
                cur0 = Lim(cur0); cur1 = Lim(cur1); cur2 = Lim(cur2);
                cur0 = Math.Clamp(cur0 + input[pos], 0, max);
                cur1 = Math.Clamp(cur1 + input[pos + 1], 0, max);
                cur2 = Math.Clamp(cur2 + input[pos + 2], 0, max);
                int pixcode = Lookup(cur0, cur1, cur2);
                codes[outPos] = pixcode;
                cur0 -= Colormap[0][pixcode];
                cur1 -= Colormap[1][pixcode];
                cur2 -= Colormap[2][pixcode];
                int bnext = cur0; err[e] = F(bprev0 + cur0 * 3); bprev0 = below0 + cur0 * 5; below0 = bnext; cur0 *= 7;
                bnext = cur1; err[e + 1] = F(bprev1 + cur1 * 3); bprev1 = below1 + cur1 * 5; below1 = bnext; cur1 *= 7;
                bnext = cur2; err[e + 2] = F(bprev2 + cur2 * 3); bprev2 = below2 + cur2 * 5; below2 = bnext; cur2 *= 7;
                pos += dir3;
                outPos += dir;
                e += dir3;
            }
            err[e] = F(bprev0);
            err[e + 1] = F(bprev1);
            err[e + 2] = F(bprev2);
        }
    }
}
