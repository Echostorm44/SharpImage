using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SharpImage.Formats.Av1;

namespace SharpImage.Formats;

/// <summary>
/// AV1 film grain synthesis parameters (spec 6.8.20), in libaom's aom_film_grain_t conventions: chroma multipliers /
/// offsets are the coded values (mult 0..255 with 128 = 0, offset 0..511 with 256 = 0) and AR coefficients are signed.
/// Signalled in the colour stream's frame header; decoders (dav1d, libavif, browsers, this library) synthesize the
/// grain on output. Use <see cref="TestVector"/> for libaom's built-in vectors (--film-grain-test),
/// <see cref="ParseTable"/> for a libaom grain table (--film-grain-table), or estimate it from the image with
/// <see cref="AvifEncodeOptions.DenoiseNoiseLevel"/>.
/// </summary>
public sealed class AvifFilmGrain
{
    public ushort RandomSeed { get; set; }
    /// <summary>Luma scaling function: up to 14 (value, scaling) points with increasing values.</summary>
    public (int Value, int Scaling)[] ScalingPointsY { get; set; } = [];
    /// <summary>Cb / Cr scaling functions: up to 10 points each.</summary>
    public (int Value, int Scaling)[] ScalingPointsCb { get; set; } = [];
    public (int Value, int Scaling)[] ScalingPointsCr { get; set; } = [];
    public bool ChromaScalingFromLuma { get; set; }
    /// <summary>8..11.</summary>
    public int ScalingShift { get; set; } = 8;
    /// <summary>Auto-regressive filter lag, 0..3 (2*lag*(lag+1) luma coefficients, one more per chroma plane).</summary>
    public int ArCoeffLag { get; set; }
    public int[] ArCoeffsY { get; set; } = new int[24];
    public int[] ArCoeffsCb { get; set; } = new int[25];
    public int[] ArCoeffsCr { get; set; } = new int[25];
    /// <summary>6..9.</summary>
    public int ArCoeffShift { get; set; } = 6;
    /// <summary>0..3.</summary>
    public int GrainScaleShift { get; set; }
    public int CbMult { get; set; } = 128;
    public int CbLumaMult { get; set; } = 192;
    public int CbOffset { get; set; } = 256;
    public int CrMult { get; set; } = 128;
    public int CrLumaMult { get; set; } = 192;
    public int CrOffset { get; set; } = 256;
    public bool OverlapFlag { get; set; }
    public bool ClipToRestrictedRange { get; set; }

    public AvifFilmGrain Clone()
    {
        var c = (AvifFilmGrain)MemberwiseClone();
        c.ScalingPointsY = (ScalingPointsY ?? []).ToArray();
        c.ScalingPointsCb = (ScalingPointsCb ?? []).ToArray();
        c.ScalingPointsCr = (ScalingPointsCr ?? []).ToArray();
        c.ArCoeffsY = Pad(ArCoeffsY, 24);
        c.ArCoeffsCb = Pad(ArCoeffsCb, 25);
        c.ArCoeffsCr = Pad(ArCoeffsCr, 25);
        return c;
    }

    private static int[] Pad(int[]? a, int n)
    {
        var r = new int[n];
        if (a != null) Array.Copy(a, r, Math.Min(n, a.Length));
        return r;
    }

    /// <summary>libaom's film grain test vector <paramref name="index"/> (1..16, aomenc --film-grain-test).</summary>
    public static AvifFilmGrain TestVector(int index)
    {
        if (index < 1 || index > TestVectors.Length)
            throw new ArgumentOutOfRangeException(nameof(index), "libaom film grain test vectors are numbered 1..16.");
        return TestVectors[index - 1].Clone();
    }

    /// <summary>Parses the first entry of a libaom film grain table ("filmgrn1" text, aomenc --film-grain-table /
    /// libaom's noise-model output). Tables carry no clip_to_restricted_range (it reads as false).</summary>
    public static AvifFilmGrain ParseTable(string text)
    {
        var tok = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int p = 0;
        string Next() => p < tok.Length ? tok[p++] : throw new FormatException("Truncated film grain table.");
        int Int() => int.Parse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        void Expect(string s)
        {
            if (Next() != s) throw new FormatException($"Film grain table: expected '{s}'.");
        }

        Expect("filmgrn1");
        Expect("E");
        Next(); Next();   // start / end time
        bool apply = Int() != 0;
        var g = new AvifFilmGrain { RandomSeed = (ushort)Int() };
        bool update = Int() != 0;
        if (!apply || !update)
            throw new FormatException("The first film grain table entry must apply grain and define its parameters.");
        Expect("p");
        g.ArCoeffLag = Int(); g.ArCoeffShift = Int(); g.GrainScaleShift = Int(); g.ScalingShift = Int();
        g.ChromaScalingFromLuma = Int() != 0; g.OverlapFlag = Int() != 0;
        g.CbMult = Int(); g.CbLumaMult = Int(); g.CbOffset = Int(); g.CrMult = Int(); g.CrLumaMult = Int(); g.CrOffset = Int();
        (int, int)[] Points(string tag)
        {
            Expect(tag);
            var pts = new (int, int)[Int()];
            for (int i = 0; i < pts.Length; i++) pts[i] = (Int(), Int());
            return pts;
        }
        g.ScalingPointsY = Points("sY");
        g.ScalingPointsCb = Points("sCb");
        g.ScalingPointsCr = Points("sCr");
        int n = 2 * g.ArCoeffLag * (g.ArCoeffLag + 1);
        Expect("cY");
        for (int i = 0; i < n; i++) g.ArCoeffsY[i] = Int();
        Expect("cCb");
        for (int i = 0; i <= n; i++) g.ArCoeffsCb[i] = Int();
        Expect("cCr");
        for (int i = 0; i <= n; i++) g.ArCoeffsCr[i] = Int();
        return g;
    }

    /// <summary>Writes these parameters as a one-entry libaom film grain table (aomenc --film-grain-table reads it).</summary>
    public string ToTable()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("filmgrn1\n");
        sb.Append(ci, $"E 0 9223372036854775807 1 {RandomSeed} 1\n");
        sb.Append(ci, $"\tp {ArCoeffLag} {ArCoeffShift} {GrainScaleShift} {ScalingShift} {(ChromaScalingFromLuma ? 1 : 0)} {(OverlapFlag ? 1 : 0)} {CbMult} {CbLumaMult} {CbOffset} {CrMult} {CrLumaMult} {CrOffset}\n");
        sb.Append(ci, $"\tsY {ScalingPointsY.Length} ");
        foreach (var (v, s) in ScalingPointsY) sb.Append(ci, $" {v} {s}");
        sb.Append(ci, $"\n\tsCb {ScalingPointsCb.Length}");
        foreach (var (v, s) in ScalingPointsCb) sb.Append(ci, $" {v} {s}");
        sb.Append(ci, $"\n\tsCr {ScalingPointsCr.Length}");
        foreach (var (v, s) in ScalingPointsCr) sb.Append(ci, $" {v} {s}");
        int n = 2 * ArCoeffLag * (ArCoeffLag + 1);
        sb.Append("\n\tcY");
        for (int i = 0; i < n; i++) sb.Append(ci, $" {ArCoeffsY[i]}");
        sb.Append("\n\tcCb");
        for (int i = 0; i <= n; i++) sb.Append(ci, $" {ArCoeffsCb[i]}");
        sb.Append("\n\tcCr");
        for (int i = 0; i <= n; i++) sb.Append(ci, $" {ArCoeffsCr[i]}");
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Validates against the bitstream constraints for the given stream and converts to the decoder-side form
    /// (dav1d conventions). Monochrome drops the chroma parameters (libaom reset_film_grain_chroma_params).</summary>
    internal Av1FilmGrainData ToAv1(bool monochrome, int ssX, int ssY)
    {
        var y = ScalingPointsY ?? [];
        bool csfl = !monochrome && ChromaScalingFromLuma;
        var cb = monochrome || csfl ? [] : ScalingPointsCb ?? [];
        var cr = monochrome || csfl ? [] : ScalingPointsCr ?? [];
        if (y.Length > 14 || cb.Length > 10 || cr.Length > 10)
            throw new ArgumentException("Film grain allows at most 14 luma and 10 points per chroma plane.");
        static void Check((int Value, int Scaling)[] pts, string name)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                if ((uint)pts[i].Value > 255 || (uint)pts[i].Scaling > 255)
                    throw new ArgumentException($"Film grain {name} points must be 0..255.");
                if (i > 0 && pts[i].Value <= pts[i - 1].Value)
                    throw new ArgumentException($"Film grain {name} point values must increase.");
            }
        }
        Check(y, "Y");
        Check(cb, "Cb");
        Check(cr, "Cr");
        if (ssX == 1 && ssY == 1 && ((y.Length == 0 && (cb.Length > 0 || cr.Length > 0)) || (cb.Length > 0) != (cr.Length > 0)))
            throw new ArgumentException("4:2:0 film grain needs luma points for chroma grain, and both chroma planes or neither.");
        if (ScalingShift is < 8 or > 11 || ArCoeffLag is < 0 or > 3 || ArCoeffShift is < 6 or > 9 || GrainScaleShift is < 0 or > 3)
            throw new ArgumentException("Film grain scaling shift (8..11), AR lag (0..3), AR shift (6..9) or grain scale shift (0..3) out of range.");
        if (CbMult is < 0 or > 255 || CbLumaMult is < 0 or > 255 || CrMult is < 0 or > 255 || CrLumaMult is < 0 or > 255
            || CbOffset is < 0 or > 511 || CrOffset is < 0 or > 511)
            throw new ArgumentException("Film grain chroma multipliers must be 0..255 and offsets 0..511.");

        int n = 2 * ArCoeffLag * (ArCoeffLag + 1);
        int[] ay = Pad(ArCoeffsY, 24), acb = Pad(ArCoeffsCb, 25), acr = Pad(ArCoeffsCr, 25);
        if (ay.Take(n).Concat(acb.Take(n + 1)).Concat(acr.Take(n + 1)).Any(c => c is < -128 or > 127))
            throw new ArgumentException("Film grain AR coefficients must be -128..127.");

        var d = new Av1FilmGrainData
        {
            Seed = RandomSeed, NumYPoints = y.Length, ChromaScalingFromLuma = csfl ? 1 : 0,
            NumUvPoints0 = cb.Length, NumUvPoints1 = cr.Length, ScalingShift = ScalingShift, ArCoeffLag = ArCoeffLag,
            ArCoeffShift = ArCoeffShift, GrainScaleShift = GrainScaleShift,
            OverlapFlag = OverlapFlag ? 1 : 0, ClipToRestrictedRange = ClipToRestrictedRange ? 1 : 0,
        };
        if (cb.Length > 0) { d.UvMult0 = CbMult - 128; d.UvLumaMult0 = CbLumaMult - 128; d.UvOffset0 = CbOffset - 256; }
        if (cr.Length > 0) { d.UvMult1 = CrMult - 128; d.UvLumaMult1 = CrLumaMult - 128; d.UvOffset1 = CrOffset - 256; }
        unsafe
        {
            for (int i = 0; i < y.Length; i++) { d.YPoints[i * 2] = (byte)y[i].Value; d.YPoints[i * 2 + 1] = (byte)y[i].Scaling; }
            for (int i = 0; i < cb.Length; i++) { d.UvPoints[i * 2] = (byte)cb[i].Value; d.UvPoints[i * 2 + 1] = (byte)cb[i].Scaling; }
            for (int i = 0; i < cr.Length; i++) { d.UvPoints[20 + i * 2] = (byte)cr[i].Value; d.UvPoints[21 + i * 2] = (byte)cr[i].Scaling; }
            if (y.Length > 0)
                for (int i = 0; i < n; i++) d.ArCoeffsY[i] = (sbyte)ay[i];
            int nuv = n + (y.Length > 0 ? 1 : 0);
            if (cb.Length > 0 || csfl)
                for (int i = 0; i < nuv; i++) d.ArCoeffsUv[i] = (sbyte)acb[i];
            if (cr.Length > 0 || csfl)
                for (int i = 0; i < nuv; i++) d.ArCoeffsUv[28 + i] = (sbyte)acr[i];
        }
        return d;
    }

    /// <summary>The inverse of <see cref="ToAv1"/>: decoder-side parameters in libaom conventions.</summary>
    internal static unsafe AvifFilmGrain FromAv1(in Av1FilmGrainData d)
    {
        var c = d;
        var g = new AvifFilmGrain
        {
            RandomSeed = (ushort)c.Seed, ChromaScalingFromLuma = c.ChromaScalingFromLuma != 0, ScalingShift = c.ScalingShift,
            ArCoeffLag = c.ArCoeffLag, ArCoeffShift = (int)c.ArCoeffShift, GrainScaleShift = c.GrainScaleShift,
            CbMult = c.UvMult0 + 128, CbLumaMult = c.UvLumaMult0 + 128, CbOffset = c.UvOffset0 + 256,
            CrMult = c.UvMult1 + 128, CrLumaMult = c.UvLumaMult1 + 128, CrOffset = c.UvOffset1 + 256,
            OverlapFlag = c.OverlapFlag != 0, ClipToRestrictedRange = c.ClipToRestrictedRange != 0,
            ScalingPointsY = new (int, int)[c.NumYPoints], ScalingPointsCb = new (int, int)[c.NumUvPoints0],
            ScalingPointsCr = new (int, int)[c.NumUvPoints1],
        };
        for (int i = 0; i < c.NumYPoints; i++) g.ScalingPointsY[i] = (c.YPoints[i * 2], c.YPoints[i * 2 + 1]);
        for (int i = 0; i < c.NumUvPoints0; i++) g.ScalingPointsCb[i] = (c.UvPoints[i * 2], c.UvPoints[i * 2 + 1]);
        for (int i = 0; i < c.NumUvPoints1; i++) g.ScalingPointsCr[i] = (c.UvPoints[20 + i * 2], c.UvPoints[21 + i * 2]);
        for (int i = 0; i < 24; i++) g.ArCoeffsY[i] = c.ArCoeffsY[i];
        for (int i = 0; i < 25; i++) { g.ArCoeffsCb[i] = c.ArCoeffsUv[i]; g.ArCoeffsCr[i] = c.ArCoeffsUv[28 + i]; }
        return g;
    }

    // libaom av1/encoder/grain_test_vectors.h (BSD-2), generated.
    private static readonly AvifFilmGrain[] TestVectors =
    [
        new() { RandomSeed = 45231, ScalingPointsY = [(16, 0), (25, 136), (33, 144), (41, 160), (48, 168), (56, 136), (67, 128), (82, 144), (97, 152), (113, 144), (128, 176), (143, 168), (158, 176), (178, 184)], ScalingPointsCb = [(16, 0), (20, 64), (28, 88), (60, 104), (90, 136), (105, 160), (134, 168), (168, 208)], ScalingPointsCr = [(16, 0), (28, 96), (56, 80), (66, 96), (80, 104), (108, 96), (122, 112), (137, 112), (169, 176)], ScalingShift = 11, ArCoeffLag = 2, ArCoeffsY = [0, 0, -58, 0, 0, 0, -76, 100, -43, 0, -51, 82, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [0, 0, -49, 0, 0, 0, -36, 22, -30, 0, -38, 7, 39, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, -47, 0, 0, 0, -31, 31, -25, 0, -32, 13, -100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 247, CbLumaMult = 192, CbOffset = 18, CrMult = 229, CrLumaMult = 192, CrOffset = 54, OverlapFlag = false, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 96), (255, 96)], ScalingPointsCb = [(0, 64), (255, 64)], ScalingPointsCr = [(0, 64), (255, 64)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 192), (255, 192)], ScalingPointsCb = [(0, 128), (255, 128)], ScalingPointsCr = [(0, 128), (255, 128)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [4, -7, 2, 4, 12, -12, 5, -8, 6, 8, -19, -16, 19, -10, -2, 17, -42, 58, -2, -13, 9, 14, -36, 67, 0], ArCoeffsCr = [4, -7, 2, 4, 12, -12, 5, -8, 6, 8, -19, -16, 19, -10, -2, 17, -42, 58, -2, -13, 9, 14, -36, 67, 0], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 1 },
        new() { RandomSeed = 45231, ScalingPointsY = [(16, 0), (24, 137), (53, 146), (63, 155), (78, 155), (107, 150), (122, 147), (136, 147), (166, 153)], ScalingPointsCb = [(16, 0), (20, 72), (27, 82), (33, 91), (69, 121), (95, 143), (108, 154), (134, 169), (147, 177)], ScalingPointsCr = [(16, 0), (24, 95), (54, 93), (65, 94), (79, 98), (109, 107), (124, 119), (139, 136), (169, 170)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [7, -9, 2, 4, 7, -12, 7, -18, 18, -30, -27, -42, 13, -20, 7, -18, 6, 107, 55, -2, -4, -9, -22, 113], ArCoeffsCb = [-3, -1, -4, 3, -6, -2, 3, 1, -4, -10, -10, -5, -5, -3, -1, -13, -28, -25, -31, -6, -4, 14, -64, 66, 0], ArCoeffsCr = [0, 4, -3, 13, 0, 1, -3, 0, -3, -10, -68, -4, -2, -5, 2, -3, -20, 62, -31, 0, -4, -1, -8, -29, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 1063, ScalingPointsY = [(0, 64), (255, 64)], ScalingPointsCb = [(0, 96), (32, 90), (64, 83), (96, 76), (128, 68), (159, 59), (191, 48), (223, 34), (255, 0)], ScalingPointsCr = [(0, 0), (32, 34), (64, 48), (96, 59), (128, 68), (159, 76), (191, 83), (223, 90), (255, 96)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [-2, 2, -5, 7, -6, 4, -2, -1, 1, -2, 0, -2, 2, -3, -5, 13, -13, 6, -14, 8, -1, 18, -36, 58, 0], ArCoeffsCr = [-2, -1, -3, 14, -4, -1, -3, 0, -1, 7, -31, 7, 2, 0, 1, 0, -7, 50, -8, -2, 2, 2, 2, -4, 0], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 2754, ScalingPointsY = [(0, 96), (20, 92), (39, 88), (59, 84), (78, 80), (98, 75), (118, 70), (137, 65), (157, 60), (177, 53), (196, 46), (216, 38), (235, 27), (255, 0)], ScalingPointsCb = [], ScalingPointsCr = [], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 0), (20, 27), (39, 38), (59, 46), (78, 53), (98, 60), (118, 65), (137, 70), (157, 75), (177, 80), (196, 84), (216, 88), (235, 92), (255, 96)], ScalingPointsCb = [(0, 0), (255, 0)], ScalingPointsCr = [(0, 0), (255, 0)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 96), (255, 96)], ScalingPointsCb = [(0, 62), (255, 62)], ScalingPointsCr = [(0, 62), (255, 62)], ScalingShift = 11, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [0, -2, -2, 8, 5, -1, 1, -1, 5, 16, -33, -9, 6, -1, -3, 10, -47, 63, 0, -15, 3, 11, -42, 75, -69], ArCoeffsCr = [1, -1, -1, 9, 5, 0, 1, -1, 5, 15, -32, -10, 8, -2, -4, 11, -46, 62, 1, -16, 3, 13, -43, 75, -55], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 48), (255, 48)], ScalingPointsCb = [(0, 32), (255, 32)], ScalingPointsCr = [(0, 32), (255, 32)], ScalingShift = 10, ArCoeffLag = 2, ArCoeffsY = [10, -30, -20, -39, 1, -24, 12, 103, 60, -9, -24, 113, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 127, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 48), (255, 48)], ScalingPointsCb = [(0, 32), (255, 32)], ScalingPointsCr = [(0, 32), (255, 32)], ScalingShift = 10, ArCoeffLag = 2, ArCoeffsY = [10, -30, -20, -39, 1, -24, 12, 103, 60, -9, -24, 113, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [-7, -6, -48, -22, 2, -3, -45, 73, -11, -26, -52, 76, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [-7, -6, -48, -22, 2, -3, -45, 73, -11, -26, -52, 76, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 1357, ScalingPointsY = [(0, 32), (255, 32)], ScalingPointsCb = [(0, 48), (32, 45), (64, 42), (96, 38), (128, 34), (159, 29), (191, 24), (223, 17), (255, 0)], ScalingPointsCr = [(0, 0), (32, 17), (64, 24), (96, 29), (128, 34), (159, 38), (191, 42), (223, 45), (255, 48)], ScalingShift = 10, ArCoeffLag = 3, ArCoeffsY = [7, -9, 2, 4, 7, -12, 7, -18, 18, -30, -27, -42, 13, -20, 7, -18, 6, 107, 55, -2, -4, -9, -22, 113], ArCoeffsCb = [-3, -1, -4, 3, -6, -2, 3, 1, -4, -10, -10, -5, -5, -3, -1, -13, -28, -25, -31, -6, -4, 14, -64, 66, 0], ArCoeffsCr = [0, 4, -3, 13, 0, 1, -3, 0, -3, -10, -68, -4, -2, -5, 2, -3, -20, 62, -31, 0, -4, -1, -8, -29, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(16, 0), (24, 49), (39, 69), (46, 84), (53, 91), (63, 100), (78, 114), (92, 134), (164, 139)], ScalingPointsCb = [(16, 0), (20, 31), (26, 42), (33, 54), (40, 65), (47, 72), (56, 85), (84, 123), (152, 157)], ScalingPointsCr = [(16, 0), (25, 14), (39, 33), (47, 40), (54, 47), (64, 62), (79, 76), (94, 83), (167, 101)], ScalingShift = 10, ArCoeffLag = 2, ArCoeffsY = [0, 0, -58, 0, 0, 0, -76, 100, -43, 0, -51, 82, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [0, 0, -49, 0, 0, 0, -36, 22, -30, 0, -38, 7, 39, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, -47, 0, 0, 0, -31, 31, -25, 0, -32, 13, -100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = false, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 48), (20, 46), (39, 44), (59, 42), (78, 40), (98, 38), (118, 35), (137, 33), (157, 30), (177, 27), (196, 23), (216, 19), (235, 13), (255, 0)], ScalingPointsCb = [], ScalingPointsCr = [], ScalingShift = 10, ArCoeffLag = 2, ArCoeffsY = [10, -30, -20, -39, 1, -24, 12, 103, 60, -9, -24, 113, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 0), (20, 13), (39, 19), (59, 23), (78, 27), (98, 30), (118, 33), (137, 35), (157, 38), (177, 40), (196, 42), (216, 44), (235, 46), (255, 48)], ScalingPointsCb = [], ScalingPointsCr = [], ScalingShift = 10, ArCoeffLag = 2, ArCoeffsY = [10, -30, -20, -39, 1, -24, 12, 103, 60, -9, -24, 113, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 8, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = true, ChromaScalingFromLuma = false, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(0, 96)], ScalingPointsCb = [], ScalingPointsCr = [], ScalingShift = 11, ArCoeffLag = 2, ArCoeffsY = [5, -15, -10, -19, 0, -12, 6, 51, 30, -5, -12, 56, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCb = [2, 2, -24, -5, 1, 1, -18, 37, -2, 0, -15, 39, -70, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffsCr = [2, 3, -24, -5, -1, 0, -18, 38, -2, 0, -15, 39, -55, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = true, GrainScaleShift = 0 },
        new() { RandomSeed = 45231, ScalingPointsY = [(16, 0), (58, 126), (87, 120), (97, 122), (112, 125), (126, 131), (141, 139), (199, 153)], ScalingPointsCb = [(16, 0), (59, 68), (66, 76), (73, 82), (79, 85), (86, 86), (151, 95), (192, 101)], ScalingPointsCr = [(16, 0), (59, 64), (89, 80), (99, 86), (114, 90), (129, 93), (144, 97), (203, 85)], ScalingShift = 10, ArCoeffLag = 3, ArCoeffsY = [4, 1, 3, 0, 1, -3, 8, -3, 7, -23, 1, -25, 0, -10, 6, -17, -4, 53, 36, 5, -5, -17, 8, 66], ArCoeffsCb = [0, -2, -2, 8, 5, -1, 1, -1, 5, 16, -33, -9, 6, -1, -3, 10, -47, 63, 0, -15, 3, 11, -42, 75, -69], ArCoeffsCr = [1, -1, -1, 9, 5, 0, 1, -1, 5, 15, -32, -10, 8, -2, -4, 11, -46, 62, 1, -16, 3, 13, -43, 75, -55], ArCoeffShift = 7, CbMult = 128, CbLumaMult = 192, CbOffset = 256, CrMult = 128, CrLumaMult = 192, CrOffset = 256, OverlapFlag = true, ClipToRestrictedRange = false, ChromaScalingFromLuma = false, GrainScaleShift = 2 },
    ];
}
