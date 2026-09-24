using System.Globalization;
using System.Text;
using SharpImage.Image;

namespace SharpImage.Formats;

/// <summary>
/// YUV4MPEG2 (.y4m) — raw planar Y'CbCr frames, the way libavif reads and writes them (avifenc input, avifdec output):
/// colour spaces C420jpeg / C420 / C420mpeg2 / C420paldv / C422 / C444 / C444alpha / Cmono and the p10 / p12 (mono10 /
/// mono12) depths, XCOLORRANGE=FULL|LIMITED (limited by default), F frame rate, one FRAME per picture; samples above 8
/// bits are 16-bit little-endian.
/// <para>Read as an <see cref="ImageFrame"/> the planes are converted to RGB (BT.601 matrix; BT.709 primaries and sRGB
/// transfer, the CICP avifenc writes for Y4M input) and also kept with the frame, so <see cref="HeifCoder.EncodeAvif(ImageFrame, AvifEncodeOptions?)"/>
/// codes them as they are (no YUV -> RGB -> YUV round trip), adopting their layout and depth when the options say
/// Auto / 0 — avifenc's Y4M behaviour.</para>
/// </summary>
public static class Y4mCoder
{
    private static readonly byte[] Magic = "YUV4MPEG2 "u8.ToArray();

    /// <summary>True when the data starts with the YUV4MPEG2 signature.</summary>
    public static bool CanDecode(ReadOnlySpan<byte> data) => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    /// <summary>A Y4M stream: its frames and frame rate.</summary>
    public sealed class Y4mStream
    {
        public required List<YuvImage> Frames { get; init; }
        public int FrameRateNumerator { get; init; } = 25;
        public int FrameRateDenominator { get; init; } = 1;
    }

    /// <summary>Reads every frame of a Y4M stream at its coded precision.</summary>
    public static Y4mStream ReadYuv(byte[] data)
    {
        if (!CanDecode(data)) throw new InvalidDataException("Not a YUV4MPEG2 stream.");
        int pos = 0;
        string header = ReadLine(data, ref pos);
        int w = 0, h = 0, fn = 25, fd = 1;
        string cs = "C420jpeg";
        bool full = false;
        foreach (var tok in header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            switch (tok[0])
            {
                case 'W': w = int.Parse(tok.AsSpan(1), CultureInfo.InvariantCulture); break;
                case 'H': h = int.Parse(tok.AsSpan(1), CultureInfo.InvariantCulture); break;
                case 'F':
                    var f = tok[1..].Split(':');
                    if (f.Length == 2 && int.TryParse(f[0], out int n) && int.TryParse(f[1], out int d) && n > 0 && d > 0) { fn = n; fd = d; }
                    break;
                case 'C': cs = tok; break;
                case 'X':
                    if (tok == "XCOLORRANGE=FULL") full = true;
                    else if (tok == "XCOLORRANGE=LIMITED") full = false;
                    break;
            }
        }
        if (w <= 0 || h <= 0) throw new InvalidDataException("Y4M header without a valid W / H.");
        var (sub, depth, alpha, csp) = cs switch
        {
            "C420jpeg" or "C420" => (AvifChromaSubsampling.Yuv420, 8, false, 0),
            "C420mpeg2" => (AvifChromaSubsampling.Yuv420, 8, false, 1),
            "C420paldv" => (AvifChromaSubsampling.Yuv420, 8, false, 2),
            "C420p10" => (AvifChromaSubsampling.Yuv420, 10, false, 0),
            "C420p12" => (AvifChromaSubsampling.Yuv420, 12, false, 0),
            "C422" => (AvifChromaSubsampling.Yuv422, 8, false, 0),
            "C422p10" => (AvifChromaSubsampling.Yuv422, 10, false, 0),
            "C422p12" => (AvifChromaSubsampling.Yuv422, 12, false, 0),
            "C444" => (AvifChromaSubsampling.Yuv444, 8, false, 0),
            "C444alpha" => (AvifChromaSubsampling.Yuv444, 8, true, 0),
            "C444p10" => (AvifChromaSubsampling.Yuv444, 10, false, 0),
            "C444p12" => (AvifChromaSubsampling.Yuv444, 12, false, 0),
            "Cmono" => (AvifChromaSubsampling.Yuv400, 8, false, 0),
            "Cmono10" => (AvifChromaSubsampling.Yuv400, 10, false, 0),
            "Cmono12" => (AvifChromaSubsampling.Yuv400, 12, false, 0),
            _ => throw new NotSupportedException($"Unsupported Y4M colour space '{cs}'."),
        };
        int cw = sub is AvifChromaSubsampling.Yuv420 or AvifChromaSubsampling.Yuv422 ? (w + 1) >> 1 : w;
        int ch = sub == AvifChromaSubsampling.Yuv420 ? (h + 1) >> 1 : h;
        int bps = depth > 8 ? 2 : 1;
        var frames = new List<YuvImage>();
        while (pos < data.Length)
        {
            string fl = ReadLine(data, ref pos);
            if (!fl.StartsWith("FRAME", StringComparison.Ordinal)) throw new InvalidDataException("Y4M frame without a FRAME marker.");
            ushort[] Plane(int pw, int ph)
            {
                long need = (long)pw * ph * bps;
                if (pos + need > data.Length) throw new InvalidDataException("Truncated Y4M frame.");
                var p = new ushort[pw * ph];
                if (bps == 1) for (int i = 0; i < p.Length; i++) p[i] = data[pos + i];
                else for (int i = 0; i < p.Length; i++) p[i] = (ushort)(data[pos + 2 * i] | data[pos + 2 * i + 1] << 8);
                pos += (int)need;
                return p;
            }
            var y = Plane(w, h);
            ushort[]? u = null, v = null, a = null;
            if (sub != AvifChromaSubsampling.Yuv400) { u = Plane(cw, ch); v = Plane(cw, ch); }
            if (alpha) a = Plane(w, h);
            frames.Add(new YuvImage
            {
                Width = w, Height = h, Depth = depth, Subsampling = sub, FullRange = full, ChromaSamplePosition = csp,
                Y = y, U = u, V = v, Alpha = a,
            });
        }
        if (frames.Count == 0) throw new InvalidDataException("Y4M stream without frames.");
        return new Y4mStream { Frames = frames, FrameRateNumerator = fn, FrameRateDenominator = fd };
    }

    private static string ReadLine(byte[] data, ref int pos)
    {
        int end = Array.IndexOf(data, (byte)'\n', pos);
        if (end < 0 || end - pos > 4096) throw new InvalidDataException("Malformed Y4M header line.");
        string s = Encoding.ASCII.GetString(data, pos, end - pos);
        pos = end + 1;
        return s;
    }

    /// <summary>Writes frames as a Y4M stream (avifdec's header: W H F Ip A0:0 C XCOLORRANGE). Every frame must share
    /// the first frame's size, depth and layout; 16-bit samples are written as C444p16-style planes.</summary>
    public static void WriteYuv(Stream stream, IReadOnlyList<YuvImage> frames, int frameRateNumerator = 25, int frameRateDenominator = 1)
    {
        if (frames.Count == 0) throw new ArgumentException("No frames.", nameof(frames));
        var f0 = frames[0];
        // Colour space tag plus ffmpeg's XYSCSS twin, as libavif's y4m writer emits them.
        string d = f0.Depth == 8 ? "" : $"p{f0.Depth}", dx = f0.Depth == 8 ? "" : $"P{f0.Depth}";
        string cs = f0.Subsampling switch
        {
            AvifChromaSubsampling.Yuv400 => (f0.Depth == 8 ? "Cmono" : $"Cmono{f0.Depth}") + " XYSCSS=400",
            AvifChromaSubsampling.Yuv420 => f0.Depth == 8
                ? f0.ChromaSamplePosition switch { 1 => "C420mpeg2 XYSCSS=420MPEG2", 2 => "C420paldv XYSCSS=420PALDV", _ => "C420jpeg XYSCSS=420JPEG" }
                : $"C420{d} XYSCSS=420{dx}",
            AvifChromaSubsampling.Yuv422 => $"C422{d} XYSCSS=422{dx}",
            _ => (f0.Depth == 8 && f0.Alpha != null ? "C444alpha" : $"C444{d}") + $" XYSCSS=444{dx}",
        };
        var hdr = $"YUV4MPEG2 W{f0.Width} H{f0.Height} F{frameRateNumerator}:{frameRateDenominator} Ip A0:0 {cs} XCOLORRANGE={(f0.FullRange ? "FULL" : "LIMITED")}\n";
        stream.Write(Encoding.ASCII.GetBytes(hdr));
        foreach (var f in frames)
        {
            if (f.Width != f0.Width || f.Height != f0.Height || f.Depth != f0.Depth || f.Subsampling != f0.Subsampling)
                throw new ArgumentException("Every Y4M frame must have the first frame's size, depth and layout.", nameof(frames));
            stream.Write("FRAME\n"u8);
            void Plane(ushort[] p)
            {
                if (f.Depth == 8) { var b = new byte[p.Length]; for (int i = 0; i < p.Length; i++) b[i] = (byte)p[i]; stream.Write(b); }
                else { var b = new byte[p.Length * 2]; for (int i = 0; i < p.Length; i++) { b[2 * i] = (byte)p[i]; b[2 * i + 1] = (byte)(p[i] >> 8); } stream.Write(b); }
            }
            Plane(f.Y);
            if (f.Subsampling != AvifChromaSubsampling.Yuv400) { Plane(f.U!); Plane(f.V!); }
            if (f.Alpha != null && f.Subsampling == AvifChromaSubsampling.Yuv444 && f.Depth == 8) Plane(f.Alpha);
        }
    }

    /// <summary>Reads the first frame as RGB (the planes kept for AVIF encoding).</summary>
    public static ImageFrame Read(Stream stream) => Read(ReadAll(stream));

    /// <summary>Reads the first frame as RGB (the planes kept for AVIF encoding).</summary>
    public static ImageFrame Read(byte[] data) => HeifCoder.FrameFromYuv(ReadYuv(data).Frames[0]);

    /// <summary>Reads every frame as RGB (planes kept), with the stream's frame rate as the timescale / durations.</summary>
    public static ImageSequence ReadSequence(byte[] data)
    {
        var s = ReadYuv(data);
        var seq = new ImageSequence { FormatName = "Y4M", Timescale = s.FrameRateNumerator };
        foreach (var f in s.Frames)
        {
            var frame = HeifCoder.FrameFromYuv(f);
            frame.DurationTicks = s.FrameRateDenominator;
            frame.Delay = (int)Math.Round(100.0 * s.FrameRateDenominator / s.FrameRateNumerator);
            seq.AddFrame(frame);
        }
        return seq;
    }

    /// <summary>Writes an RGB image as 4:4:4 Y4M (BT.601, full range; 8-bit for 8-bit images, else 12-bit): the kept
    /// planes are written as they are when the frame was read from Y4M or decoded with its planes.</summary>
    public static void Write(ImageFrame image, Stream stream) => WriteYuv(stream, [HeifCoder.YuvFromFrame(image)]);

    /// <summary>Writes every frame (the first frame's frame duration sets the rate when a timescale is known).</summary>
    public static void WriteSequence(ImageSequence sequence, Stream stream)
    {
        var frames = sequence.Frames.Select(HeifCoder.YuvFromFrame).ToList();
        long ticks = sequence.Frames.Count > 0 ? sequence.Frames[0].DurationTicks : 0;
        if (sequence.Timescale > 0 && ticks > 0) WriteYuv(stream, frames, (int)sequence.Timescale, (int)ticks);
        else
        {
            int cs = sequence.Frames.Count > 0 && sequence.Frames[0].Delay > 0 ? sequence.Frames[0].Delay : 4;
            WriteYuv(stream, frames, 100, cs);
        }
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
