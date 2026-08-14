// Z-scan (Morton) addressing + intra reference-sample availability for the encoder, replicating
// HevcDecoder's FillReferenceSamples availability logic for a single-slice, single-tile picture.
// Lets the encoder predict per transform-unit in the quadtree with exactly the neighbours the
// decoder will consider available.
namespace SharpImage.Formats.Hevc;

internal static class HevcZScan
{
    private const int CtbSize = 32;
    private const int Log2Ctb = 5;
    private const int Log2MinTb = 2;
    private const int Log2Diff = Log2Ctb - Log2MinTb; // 3
    private const int TbMask = (1 << Log2Diff) - 1;    // 7

    // Morton interleave of (xTb, yTb) within a CTB.
    public static int Addr(int xTb, int yTb)
    {
        int val = 0;
        for (int i = 0; i < Log2Diff; i++)
        {
            int m = 1 << i;
            if ((m & xTb) != 0)
            {
                val += m * m;
            }

            if ((m & yTb) != 0)
            {
                val += 2 * m * m;
            }
        }

        return val;
    }

    /// <summary>Neighbour availability for a luma transform block at (x,y) size n in a pw×ph picture.</summary>
    public static HevcIntraPrediction.Avail LumaAvail(int x, int y, int n, int pw, int ph)
        => Avail(x, y, n, pw, ph, chromaShift: 0);

    /// <summary>Neighbour availability for a chroma transform block; x,y,n are in chroma samples.</summary>
    public static HevcIntraPrediction.Avail ChromaAvail(int x, int y, int n, int cw, int chh)
        => Avail(x, y, n, cw, chh, chromaShift: 1);

    private static HevcIntraPrediction.Avail Avail(int x, int y, int n, int planeW, int planeH, int chromaShift)
    {
        // Work in luma coordinates for z-scan address computation.
        int lumaX = x << chromaShift;
        int lumaY = y << chromaShift;
        int lumaN = n << chromaShift;
        int picLumaW = planeW << chromaShift;
        int picLumaH = planeH << chromaShift;
        int ctbsPerRow = picLumaW / CtbSize;

        int xTb = (lumaX >> Log2MinTb) & TbMask;
        int yTb = (lumaY >> Log2MinTb) & TbMask;
        int sizeInTbs = lumaN >> Log2MinTb;
        int curCtbAddr = ((lumaY / CtbSize) * ctbsPerRow) + (lumaX / CtbSize);
        int curZsLocal = Addr(xTb, yTb);

        int x0b = lumaX & (CtbSize - 1);
        int y0b = lumaY & (CtbSize - 1);

        bool ctbLeft = (lumaX / CtbSize) > 0;
        bool ctbUp = (lumaY / CtbSize) > 0;
        bool ctbUpRight = ctbUp && ((lumaX / CtbSize) + 1) < ctbsPerRow;
        bool ctbUpLeft = ctbLeft && ctbUp;

        bool candLeft = ctbLeft || x0b > 0;
        bool candUp = ctbUp || y0b > 0;
        bool candUpLeft = (x0b > 0 || y0b > 0) ? (candLeft && candUp) : ctbUpLeft;

        // Above-right.
        bool candUpRight = false;
        bool candUpRightSap = (x0b + lumaN >= CtbSize) ? (ctbUpRight && y0b == 0) : candUp;
        if (candUpRightSap && (x + n) < planeW)
        {
            int urX = lumaX + lumaN, urY = lumaY - 1;
            if (urY >= 0)
            {
                int urCtbAddr = ((urY / CtbSize) * ctbsPerRow) + (urX / CtbSize);
                if (urCtbAddr < curCtbAddr)
                {
                    candUpRight = true;
                }
                else if (urCtbAddr == curCtbAddr)
                {
                    int urZs = Addr((xTb + sizeInTbs) & TbMask, (yTb - 1) & TbMask);
                    candUpRight = curZsLocal > urZs;
                }
            }
        }

        // Below-left.
        bool candBottomLeft = false;
        int endOfCtbRowY = System.Math.Min(((lumaY / CtbSize) + 1) * CtbSize, picLumaH);
        if (candLeft && (y + n) < (endOfCtbRowY >> chromaShift))
        {
            int blX = lumaX - 1, blY = lumaY + lumaN;
            if (blX >= 0 && blY < picLumaH)
            {
                int blCtbAddr = ((blY / CtbSize) * ctbsPerRow) + (blX / CtbSize);
                if (blCtbAddr < curCtbAddr)
                {
                    candBottomLeft = true;
                }
                else if (blCtbAddr == curCtbAddr)
                {
                    int blZs = Addr((xTb - 1) & TbMask, (yTb + sizeInTbs) & TbMask);
                    candBottomLeft = curZsLocal > blZs;
                }
            }
        }

        return new HevcIntraPrediction.Avail(candUp, candLeft, candUpLeft, candUpRight, candBottomLeft);
    }
}
