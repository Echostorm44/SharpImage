// Encoder-side HEVC intra prediction (8-bit). Reproduces the decoder's exact reference-sample
// substitution, [1 2 1] / strong smoothing, and planar/DC/angular prediction so that the residual
// the encoder forms reconstructs identically in the decoder. Availability is for the encoder's
// fixed single-slice, one-CU-per-CTU (32x32) structure. Formulas mirror HevcDecoder.IntraPrediction.
using System;

namespace SharpImage.Formats.Hevc;

internal static class HevcIntraPrediction
{
    private const int IntraPlanar = 0;
    private const int IntraDc = 1;

    private static readonly int[] AngleTable =
    {
        32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26, -32,
        -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32,
    };

    private static readonly int[] InvAngleTable =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        4096, 1638, 910, 630, 482, 390, 315, 256,
        315, 390, 482, 630, 910, 1638, 4096, 0,
        0, 0, 0, 0, 0, 0, 0,
    };

    /// <summary>Neighbor availability for a transform block (single-slice raster of 32x32 CUs).</summary>
    public readonly record struct Avail(bool Up, bool Left, bool UpLeft, bool UpRight, bool BottomLeft);

    /// <summary>
    /// Predicts an <paramref name="n"/>×<paramref name="n"/> block into <paramref name="pred"/> (row-major),
    /// reading reconstructed neighbors from <paramref name="plane"/> at (px,py).
    /// </summary>
    public static void Predict(ReadOnlySpan<byte> plane, int stride, int px, int py, int n, int bitDepth,
        int cIdx, int mode, Avail avail, Span<int> pred, bool smoothingDisabled, bool strongSmoothing)
    {
        int maxVal = (1 << bitDepth) - 1;
        int dcDefault = 1 << (bitDepth - 1);

        // refAbove[0]=corner, [1..2n]=top row; refLeft[0]=corner, [1..2n]=left col.
        Span<int> refAbove = stackalloc int[(2 * n) + 1];
        Span<int> refLeft = stackalloc int[(2 * n) + 1];
        BuildReferences(plane, stride, px, py, n, avail, dcDefault, refAbove, refLeft);

        if (!smoothingDisabled && mode != IntraDc && n >= 8 && cIdx == 0)
        {
            ApplySmoothing(refAbove, refLeft, n, bitDepth, mode, strongSmoothing);
        }

        if (mode == IntraPlanar)
        {
            PredictPlanar(pred, n, refAbove, refLeft);
        }
        else if (mode == IntraDc)
        {
            PredictDc(pred, n, cIdx, refAbove, refLeft);
        }
        else
        {
            PredictAngular(pred, n, cIdx, mode, maxVal, refAbove, refLeft);
        }
    }

    private static void BuildReferences(ReadOnlySpan<byte> plane, int stride, int px, int py, int n,
        Avail avail, int dcDefault, Span<int> refAbove, Span<int> refLeft)
    {
        int total = (4 * n) + 1;
        Span<int> seq = stackalloc int[total];   // scan order: bottom-left up, corner, then across top
        Span<bool> ok = stackalloc bool[total];

        // Left column bottom→top: p[-1][2n-1] .. p[-1][0]
        for (int i = 0; i < 2 * n; i++)
        {
            int yy = (2 * n) - 1 - i;
            bool a = yy < n ? avail.Left : avail.BottomLeft;
            ok[i] = a;
            seq[i] = a ? plane[((py + yy) * stride) + (px - 1)] : 0;
        }

        // Corner p[-1][-1]
        ok[2 * n] = avail.UpLeft;
        seq[2 * n] = avail.UpLeft ? plane[((py - 1) * stride) + (px - 1)] : 0;

        // Top row left→right: p[0][-1] .. p[2n-1][-1]
        for (int i = 0; i < 2 * n; i++)
        {
            bool a = i < n ? avail.Up : avail.UpRight;
            ok[(2 * n) + 1 + i] = a;
            seq[(2 * n) + 1 + i] = a ? plane[((py - 1) * stride) + (px + i)] : 0;
        }

        // Substitution (8.4.4.2.2): if none available, all = dcDefault; else propagate from first available.
        bool any = false;
        for (int i = 0; i < total; i++)
        {
            if (ok[i]) { any = true; break; }
        }

        if (!any)
        {
            for (int i = 0; i < total; i++)
            {
                seq[i] = dcDefault;
            }
        }
        else
        {
            if (!ok[0])
            {
                int firstVal = 0;
                for (int i = 0; i < total; i++)
                {
                    if (ok[i]) { firstVal = seq[i]; break; }
                }

                seq[0] = firstVal;
            }

            for (int i = 1; i < total; i++)
            {
                if (!ok[i])
                {
                    seq[i] = seq[i - 1];
                }
            }
        }

        // Scatter back into refAbove/refLeft.
        refLeft[0] = seq[2 * n];   // corner
        refAbove[0] = seq[2 * n];
        for (int i = 0; i < 2 * n; i++)
        {
            int yy = (2 * n) - 1 - i;
            refLeft[yy + 1] = seq[i];
        }

        for (int i = 0; i < 2 * n; i++)
        {
            refAbove[i + 1] = seq[(2 * n) + 1 + i];
        }
    }

    private static void ApplySmoothing(Span<int> refAbove, Span<int> refLeft, int n, int bitDepth, int mode, bool strongEnabled)
    {
        int log2 = Log2(n);
        int minDist = Math.Min(Math.Abs(mode - 26), Math.Abs(mode - 10));
        int threshIdx = log2 - 3;
        ReadOnlySpan<int> distThresh = stackalloc int[] { 7, 1, 0 };
        if (threshIdx < 0 || threshIdx >= 3 || minDist <= distThresh[threshIdx])
        {
            return;
        }

        int threshold = 1 << (bitDepth - 5);
        if (strongEnabled && log2 == 5 &&
            Math.Abs(refAbove[0] + refAbove[2 * n] - (2 * refAbove[n])) < threshold &&
            Math.Abs(refLeft[0] + refLeft[2 * n] - (2 * refLeft[n])) < threshold)
        {
            int topCorner = refAbove[0], topEnd = refAbove[2 * n];
            for (int i = 1; i < 2 * n; i++)
            {
                refAbove[i] = (((2 * n) - i) * topCorner + (i * topEnd) + n) >> (log2 + 1);
            }

            int leftCorner = refLeft[0], leftEnd = refLeft[2 * n];
            for (int i = 1; i < 2 * n; i++)
            {
                refLeft[i] = (((2 * n) - i) * leftCorner + (i * leftEnd) + n) >> (log2 + 1);
            }

            return;
        }

        Span<int> fl = stackalloc int[(2 * n) + 1];
        Span<int> fa = stackalloc int[(2 * n) + 1];
        fl[2 * n] = refLeft[2 * n];
        for (int i = (2 * n) - 1; i >= 1; i--)
        {
            fl[i] = (refLeft[i - 1] + (2 * refLeft[i]) + refLeft[i + 1] + 2) >> 2;
        }

        fl[0] = (refLeft[1] + (2 * refLeft[0]) + refAbove[1] + 2) >> 2;

        fa[2 * n] = refAbove[2 * n];
        for (int i = (2 * n) - 1; i >= 1; i--)
        {
            fa[i] = (refAbove[i - 1] + (2 * refAbove[i]) + refAbove[i + 1] + 2) >> 2;
        }

        fa[0] = fl[0];
        fl.CopyTo(refLeft);
        fa.CopyTo(refAbove);
    }

    private static void PredictPlanar(Span<int> pred, int n, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int log2 = Log2(n);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int predV = ((n - 1 - x) * refLeft[y + 1]) + ((x + 1) * refAbove[n + 1]);
                int predH = ((n - 1 - y) * refAbove[x + 1]) + ((y + 1) * refLeft[n + 1]);
                pred[(y * n) + x] = (predV + predH + n) >> (log2 + 1);
            }
        }
    }

    private static void PredictDc(Span<int> pred, int n, int cIdx, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int log2 = Log2(n);
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += refAbove[i + 1] + refLeft[i + 1];
        }

        int dc = (sum + n) >> (log2 + 1);
        for (int i = 0; i < n * n; i++)
        {
            pred[i] = dc;
        }

        if (cIdx == 0 && n < 32)
        {
            pred[0] = (refLeft[1] + (2 * dc) + refAbove[1] + 2) >> 2;
            for (int x = 1; x < n; x++)
            {
                pred[x] = (refAbove[x + 1] + (3 * dc) + 2) >> 2;
            }

            for (int y = 1; y < n; y++)
            {
                pred[y * n] = (refLeft[y + 1] + (3 * dc) + 2) >> 2;
            }
        }
    }

    private static void PredictAngular(Span<int> pred, int n, int cIdx, int mode, int maxVal, ReadOnlySpan<int> refAbove, ReadOnlySpan<int> refLeft)
    {
        int angle = AngleTable[mode - 2];
        int offset = n;
        Span<int> refMain = stackalloc int[(3 * n) + 1];
        Span<int> refSide = stackalloc int[(2 * n) + 1];
        bool vertical = mode >= 18;

        if (vertical)
        {
            for (int i = 0; i <= 2 * n; i++) { refMain[offset + i] = refAbove[i]; }
            for (int i = 0; i <= 2 * n; i++) { refSide[i] = refLeft[i]; }
        }
        else
        {
            for (int i = 0; i <= 2 * n; i++) { refMain[offset + i] = refLeft[i]; }
            for (int i = 0; i <= 2 * n; i++) { refSide[i] = refAbove[i]; }
        }

        if (angle < 0)
        {
            int invAngle = InvAngleTable[mode - 2];
            int sum = 128;
            for (int i = -1; i >= -n; i--)
            {
                sum += invAngle;
                int refIdx = sum >> 8;
                if (refIdx >= 0 && refIdx <= 2 * n)
                {
                    refMain[offset + i] = refSide[refIdx];
                }
            }
        }

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int deltaPos = vertical ? (y + 1) * angle : (x + 1) * angle;
                int dInt = deltaPos >> 5;
                int dFrac = deltaPos & 31;
                int refIdx = (vertical ? x : y) + 1 + dInt;
                int val;
                if (dFrac != 0)
                {
                    int s0 = refMain[offset + refIdx];
                    int s1 = refMain[offset + refIdx + 1];
                    val = (((32 - dFrac) * s0) + (dFrac * s1) + 16) >> 5;
                }
                else
                {
                    val = refMain[offset + refIdx];
                }

                pred[(y * n) + x] = Math.Clamp(val, 0, maxVal);
            }
        }

        if (cIdx == 0 && n < 32)
        {
            if (mode == 26)
            {
                for (int y = 0; y < n; y++)
                {
                    pred[y * n] = Math.Clamp(refAbove[1] + ((refLeft[y + 1] - refLeft[0]) >> 1), 0, maxVal);
                }
            }
            else if (mode == 10)
            {
                for (int x = 0; x < n; x++)
                {
                    pred[x] = Math.Clamp(refLeft[1] + ((refAbove[x + 1] - refLeft[0]) >> 1), 0, maxVal);
                }
            }
        }
    }

    private static int Log2(int v) => v switch { 4 => 2, 8 => 3, 16 => 4, 32 => 5, _ => 2 };
}
