using System;

namespace SharpImage.Formats.Av1;

/// <summary>
/// The AVIF still-image path through the libaom-port encoder: one all-intra key frame of 8-bit planes encoded exactly
/// as libaom 3.14.1 does it for libavif (AOM_USAGE_ALL_INTRA, AOM_Q at the given qindex, cpu-used = the avifenc speed,
/// tune PSNR, one tile), returned as the sequence-header and frame OBUs the AVIF container stores. Used for what the
/// port covers (8-bit 4:2:0 / 4:4:4 / 4:0:0, speeds 0-9 (10 = 9, as libavif clamps), no film grain / layers / tiling / sharpness); everything else
/// stays on the earlier encoder until ported.
/// </summary>
internal static class AomStill
{
    /// <summary>SHARPIMAGE_AV1_LEGACY=1 keeps every encode on the earlier encoder.</summary>
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPIMAGE_AV1_LEGACY") != "1";

    /// <summary>The avifenc speed (cpu-used) of the current AVIF all-intra encode, + 1 (0: no such encode is active, so
    /// the port is not used).</summary>
    [ThreadStatic] private static int t_speedPlus1;
    /// <summary>The colour image is coded with libaom's tune=iq (libavif's default for non-identity colour); alpha items
    /// (no colour description) keep tune=psnr, as libavif sets for alpha.</summary>
    [ThreadStatic] private static bool t_tuneIq;

    internal static int Speed => t_speedPlus1 - 1;

    /// <summary>Activates the port for an all-intra encode at this avifenc speed (clamped to libaom's 0-9 as libavif does);
    /// a negative speed deactivates it. Returns the previous state for <see cref="Exit"/>.</summary>
    internal static (int, bool) Enter(int speed, bool tuneIq = false)
    {
        var prev = (t_speedPlus1, t_tuneIq);
        t_speedPlus1 = speed < 0 ? 0 : Math.Clamp(speed, 0, 9) + 1;
        t_tuneIq = tuneIq;
        return prev;
    }

    internal static void Exit((int SpeedPlus1, bool TuneIq) prev) => (t_speedPlus1, t_tuneIq) = prev;

    /// <summary>Whether a frame with these parameters goes through the port.</summary>
    internal static bool Handles(int bitDepth, Av1PixelLayout layout, int width, int height)
        => Enabled && bitDepth == 8 && layout is Av1PixelLayout.I420 or Av1PixelLayout.I444 or Av1PixelLayout.I400
           && t_speedPlus1 >= 1 && (!t_tuneIq || t_speedPlus1 <= 7) && Av1ObuWriter.PlainStill && width <= 4096 && width * height <= 4096 * 2304;

    /// <summary>Encodes one frame. Planes hold samples 0..255 (ushort, the earlier encoder's plane type); chroma planes
    /// are ((w + ssX) &gt;&gt; ssX) x ((h + ssY) &gt;&gt; ssY); u / v are ignored for 4:0:0. qIdx 0 codes losslessly.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) Encode(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, Av1PixelLayout layout, int qIdx, Av1ObuWriter.Av1ColorDesc? color)
    {
        bool mono = layout == Av1PixelLayout.I400;
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 || mono ? 1 : 0;
        int cw = (width + ssX) >> ssX, ch = (height + ssY) >> ssY;
        var planes = mono ? new[] { Narrow(y, width * height) } : new[] { Narrow(y, width * height), Narrow(u, cw * ch), Narrow(v, cw * ch) };
        var input = new AomEncodeInput
        {
            Width = width, Height = height, SsX = ssX, SsY = ssY, Monochrome = mono, Planes = planes,
            Strides = mono ? new[] { width } : new[] { width, cw, cw }, BaseQindex = qIdx, Speed = Speed,
            // libavif: maxThreads > 1 -> cfg.g_threads = min(maxThreads, 64) (row-MT, libaom's default)
            Threads = Math.Min(Av1StillImageEncoder.ThreadCount, 64),
            TuneIq = t_tuneIq && color != null,
        };
        var (cpi, x) = AomEncoder.EncodeFrame(input);
        AomEncoder.RunPostFilter(cpi, x, applyRestoration: false);
        var c = color ?? Av1ObuWriter.Av1ColorDesc.Legacy;
        var seqCfg = new AomSequenceConfig
        {
            ColorPrimaries = c.Primaries, TransferCharacteristics = c.Transfer, MatrixCoefficients = c.Matrix,
            ColorRange = c.FullRange ? 1 : 0, ChromaSamplePosition = layout == Av1PixelLayout.I420 ? Av1ObuWriter.ChromaSamplePosition : 0,
        };
        return SplitPacket(AomBitstream.PackFrame(cpi, seqCfg));
    }

    private static byte[] Narrow(ReadOnlySpan<ushort> src, int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)src[i];
        return b;
    }

    /// <summary>The (temporal delimiter +) sequence-header OBUs and the frame OBU of a TD + sequence header + frame
    /// packet.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) SplitPacket(byte[] packet)
    {
        byte[]? seq = null, frame = null;
        int pos = 0;
        while (pos < packet.Length)
        {
            int start = pos;
            int header = packet[pos++];
            int type = (header >> 3) & 15;
            if ((header & 4) != 0) pos++;   // extension
            if ((header & 2) == 0) throw new InvalidOperationException("OBU without size field");
            long size = 0;
            for (int shift = 0; ; shift += 7)
            {
                int b = packet[pos++];
                size |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
            }
            pos += (int)size;
            // libavif stores libaom's whole packet as the item data: the temporal delimiter travels with the header
            if (type == 1) seq = packet[0..pos];
            else if (type == 6) frame = packet[start..pos];
        }
        return (seq ?? throw new InvalidOperationException("no sequence header"), frame ?? throw new InvalidOperationException("no frame OBU"));
    }
}
