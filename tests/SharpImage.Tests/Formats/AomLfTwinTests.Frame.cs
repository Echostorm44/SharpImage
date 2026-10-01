using SharpImage.Formats.Av1;
using static SharpImage.Formats.Av1.AomTables;

namespace SharpImage.Tests.Formats;

public sealed partial class AomLfTwinTests
{
    // A random valid partition of the frame into blocks (square / rect / 4-way splits of 64x64 superblocks), each with
    // an intra transform size (the max rect size split 0..2 times), optionally some skipped inter blocks.
    private sealed class SyntheticGrid
    {
        public readonly List<(int r, int c, int bs, int tx, bool inter, int mode, int seg)> Blocks = new();
        public int[] BlkOfMi = Array.Empty<int>();
    }

    private static void Partition(Random rng, SyntheticGrid g, int r, int c, int bs, int miRows, int miCols, bool allowInter)
    {
        if (r >= miRows || c >= miCols) return;
        int n = MiSizeWide[bs];
        int choice = bs == BLOCK_8X8 ? rng.Next(4) : rng.Next(6);
        if (bs == BLOCK_4X4) choice = 0;
        if (choice == 3 || ((r + n > miRows || c + n > miCols) && bs > BLOCK_8X8))
        {
            int sub = SubsizeLookup[PARTITION_SPLIT * 6 + MiSizeWideLog2[bs]];
            int h = n / 2;
            Partition(rng, g, r, c, sub, miRows, miCols, allowInter);
            Partition(rng, g, r, c + h, sub, miRows, miCols, allowInter);
            Partition(rng, g, r + h, c, sub, miRows, miCols, allowInter);
            Partition(rng, g, r + h, c + h, sub, miRows, miCols, allowInter);
            return;
        }
        void Add(int rr, int cc, int b)
        {
            if (rr >= miRows || cc >= miCols) return;
            int tx = MaxTxsizeRectLookup[b];
            int depth = rng.Next(3);
            for (int d = 0; d < depth && tx != TX_4X4; d++) tx = SubTxSizeMap[tx];
            bool inter = allowInter && rng.Next(5) == 0;
            if (inter) tx = MaxTxsizeRectLookup[b];
            int mode = inter ? (rng.Next(2) == 0 ? 13 : 15) : rng.Next(13);
            g.Blocks.Add((rr, cc, b, tx, inter, mode, 0));
        }
        int part = choice switch { 0 => PARTITION_NONE, 1 => PARTITION_HORZ, 2 => PARTITION_VERT, 4 => PARTITION_HORZ_4, _ => PARTITION_VERT_4 };
        if ((part == PARTITION_HORZ_4 || part == PARTITION_VERT_4) && (bs == BLOCK_8X8 || bs > BLOCK_64X64)) part = PARTITION_NONE;
        int s = SubsizeLookup[part * 6 + MiSizeWideLog2[bs]];
        switch (part)
        {
            case PARTITION_NONE: Add(r, c, bs); break;
            case PARTITION_HORZ: Add(r, c, s); Add(r + n / 2, c, s); break;
            case PARTITION_VERT: Add(r, c, s); Add(r, c + n / 2, s); break;
            case PARTITION_HORZ_4: for (int i = 0; i < 4; i++) Add(r + i * n / 4, c, s); break;
            default: for (int i = 0; i < 4; i++) Add(r, c + i * n / 4, s); break;
        }
    }

    private static SyntheticGrid MakeGrid(Random rng, int miRows, int miCols, bool allowInter)
    {
        var g = new SyntheticGrid();
        for (int r = 0; r < miRows; r += 16)
            for (int c = 0; c < miCols; c += 16)
                Partition(rng, g, r, c, BLOCK_64X64, miRows, miCols, allowInter);
        g.BlkOfMi = new int[miRows * miCols];
        Array.Fill(g.BlkOfMi, -1);
        for (int b = 0; b < g.Blocks.Count; b++)
        {
            var (r, c, bs, _, _, _, _) = g.Blocks[b];
            for (int y = r; y < Math.Min(r + MiSizeHigh[bs], miRows); y++)
                for (int x = c; x < Math.Min(c + MiSizeWide[bs], miCols); x++) g.BlkOfMi[y * miCols + x] = b;
        }
        return g;
    }

    private static AomLfMiGrid ToMiGrid(SyntheticGrid g, int miRows, int miCols)
    {
        var mi = new AomLfMiGrid(miRows, miCols);
        for (int i = 0; i < miRows * miCols; i++)
        {
            int b = g.BlkOfMi[i];
            if (b < 0) continue;
            var (_, _, bs, tx, inter, mode, seg) = g.Blocks[b];
            mi.Coded[i] = true;
            mi.Bsize[i] = (byte)bs;
            mi.TxSize[i] = (byte)tx;
            mi.Skip[i] = inter;
            mi.IsInter[i] = inter;
            mi.RefFrame0[i] = (sbyte)(inter ? 1 : 0);
            mi.Mode[i] = (byte)mode;
            mi.SegmentId[i] = (byte)seg;
        }
        return mi;
    }

    [Test]
    public async Task LoopFilterFrame_SyntheticGrids()
    {
        if (!Available) return;
        var rng = new Random(21);
        int frames = 0;
        for (int iter = 0; iter < 120; iter++)
        {
            int w = rng.Next(8, 300), h = rng.Next(8, 300);
            bool s444 = rng.Next(3) == 0;
            int ss = s444 ? 0 : 1;
            int opt = rng.Next(3);                 // libaom's lpf_opt_level 0 / 1 / 2 (dual/quad and joint chroma paths)
            bool allowInter = opt == 0;            // the opt paths assume uniform intra transform sizes per block
            var frame = new AomYv12(w, h, ss, ss, 3);
            for (int p = 0; p < 3; p++) FillPlane(rng, frame.Planes[p], rng.Next(0, 12));
            int miRows = frame.Planes[0].Height >> 2, miCols = frame.Planes[0].Width >> 2;
            var g = MakeGrid(rng, miRows, miCols, allowInter);
            var mi = ToMiGrid(g, miRows, miCols);
            var lf = new AomLoopFilterParams
            {
                SharpnessLevel = rng.Next(8), FilterLevelU = rng.Next(64), FilterLevelV = rng.Next(64),
            };
            lf.FilterLevel[0] = rng.Next(64);
            lf.FilterLevel[1] = rng.Next(4) == 0 ? 0 : rng.Next(64);
            if (opt == 2) lf.FilterLevelV = lf.FilterLevelU;   // FROM_Q: u and v share a level
            // (lpf_opt_level 2 filters U and V jointly with U's level: libaom only uses it for the whole-frame
            // application at LPF_PICK_FROM_Q, never for a chroma-only call)
            int planeStart = opt != 2 && rng.Next(4) == 0 ? rng.Next(3) : 0;
            int planeEnd = 3;
            var theirs = frame.Clone();
            new AomLoopFilter().FilterFrame(frame, mi, lf, planeStart, planeEnd, false, opt);
            int n = g.Blocks.Count;
            var bsz = new byte[n];
            var txs = new byte[n];
            var skip = new byte[n];
            var ref0 = new sbyte[n];
            var mode = new byte[n];
            var seg = new byte[n];
            for (int b = 0; b < n; b++)
            {
                var blk = g.Blocks[b];
                bsz[b] = (byte)blk.bs;
                txs[b] = (byte)blk.tx;
                skip[b] = (byte)(blk.inter ? 1 : 0);
                ref0[b] = (sbyte)(blk.inter ? 1 : 0);
                mode[b] = (byte)blk.mode;
                seg[b] = (byte)blk.seg;
            }
            unsafe
            {
                fixed (byte* py = theirs.Planes[0].Buf)
                fixed (byte* pu = theirs.Planes[1].Buf)
                fixed (byte* pv = theirs.Planes[2].Buf)
                fixed (int* bom = g.BlkOfMi)
                fixed (byte* a = bsz)
                fixed (byte* t = txs)
                fixed (byte* sk = skip)
                fixed (sbyte* rf = ref0)
                fixed (byte* md = mode)
                fixed (byte* sg = seg)
                    Native.twin_lf_frame(py + theirs.Planes[0].Origin, pu + theirs.Planes[1].Origin, pv + theirs.Planes[2].Origin,
                        theirs.Planes[0].Stride, theirs.Planes[1].Stride, w, h, ss, ss, 0, miRows, miCols, bom, n, a, t, sk, rf,
                        md, sg, lf.FilterLevel[0], lf.FilterLevel[1], lf.FilterLevelU, lf.FilterLevelV, lf.SharpnessLevel,
                        planeStart, planeEnd, 0, opt, BLOCK_64X64);
            }
            for (int p = 0; p < 3; p++)
            {
                bool eq = frame.Planes[p].Buf.AsSpan().SequenceEqual(theirs.Planes[p].Buf);
                if (!eq)
                {
                    var P = frame.Planes[p];
                    var T = theirs.Planes[p];
                    int first = -1;
                    for (int i = 0; i < P.Buf.Length && first < 0; i++) if (P.Buf[i] != T.Buf[i]) first = i;
                    int fy = first / P.Stride - P.Border, fx = first % P.Stride - P.Border;
                    Console.WriteLine($"iter {iter} plane {p} {w}x{h} ss{ss} opt{opt} ps{planeStart} first diff at ({fx},{fy})");
                }
                await Assert.That(eq).IsTrue();
            }
            frames++;
        }
        await Assert.That(frames).IsEqualTo(120);
    }
}
