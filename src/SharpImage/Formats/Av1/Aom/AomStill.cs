using System;

namespace SharpImage.Formats.Av1;

/// <summary>
/// The AVIF still-image path through the libaom-port encoder: one all-intra key frame of 8-bit planes encoded exactly
/// as libaom 3.14.1 does it for libavif (AOM_USAGE_ALL_INTRA, AOM_Q at the given qindex, cpu-used = the avifenc speed,
/// tune PSNR / SSIM / IQ, uniform tiles), returned as the sequence-header and frame OBUs the AVIF container stores. Used for what the
/// port covers (8 / 10 / 12-bit 4:2:0 / 4:2:2 / 4:4:4 / 4:0:0 -- high bit depth as libavif drives it: AOM_CODEC_USE_HIGHBITDEPTH,
/// loop restoration off at 12 bits --, speeds 0-9 (10 = 9, as libavif clamps), no layers / sharpness; any tiling); everything else
/// stays on the earlier encoder until ported.
/// </summary>
internal static class AomStill
{
    /// <summary>SHARPIMAGE_AV1_LEGACY=1 keeps every encode on the earlier encoder.</summary>
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPIMAGE_AV1_LEGACY") != "1";

    /// <summary>The avifenc speed (cpu-used) of the current AVIF all-intra encode, + 1 (0: no such encode is active, so
    /// the port is not used).</summary>
    [ThreadStatic] private static int t_speedPlus1;
    /// <summary>The colour image's libaom tuning (libavif's default: tune=iq for non-identity colour, tune=ssim for the
    /// identity matrix); alpha items (no colour description) keep tune=psnr, as libavif sets for alpha.</summary>
    [ThreadStatic] private static AomTune t_tune;
    /// <summary>The user's -a sharpness / -a enable-cdef (colour and alpha alike, as libavif's unprefixed options).</summary>
    /// <summary>Grid cells: the size every cell is coded at (the first cell's); smaller edge cells are padded.</summary>
    [ThreadStatic] internal static (int Width, int Height)? t_padTo;
    [ThreadStatic] private static int? t_sharpness;
    [ThreadStatic] private static bool? t_enableCdef;
    internal static int? SharpnessOverride => t_sharpness;
    internal static bool? CdefOverride => t_enableCdef;

    internal static int Speed => t_speedPlus1 - 1;

    /// <summary>Activates the port for an all-intra encode at this avifenc speed (clamped to libaom's 0-9 as libavif does);
    /// a negative speed deactivates it. Returns the previous state for <see cref="Exit"/>.</summary>
    internal static (int, AomTune, int?, bool?) Enter(int speed, AomTune tune = AomTune.Psnr, int? sharpness = null, bool? enableCdef = null)
    {
        var prev = (t_speedPlus1, t_tune, t_sharpness, t_enableCdef);
        t_speedPlus1 = speed < 0 ? 0 : Math.Clamp(speed, 0, 9) + 1;
        t_tune = tune;
        if (speed >= 0) { t_sharpness = sharpness; t_enableCdef = enableCdef; }
        return prev;
    }

    internal static void Exit((int SpeedPlus1, AomTune Tune, int? Sharpness, bool? EnableCdef) prev)
        => (t_speedPlus1, t_tune, t_sharpness, t_enableCdef) = prev;

    /// <summary>Whether a frame with these parameters goes through the port.</summary>
    internal static bool Handles(int bitDepth, Av1PixelLayout layout, int width, int height)
        => Enabled && bitDepth is 8 or 10 or 12 && layout is Av1PixelLayout.I420 or Av1PixelLayout.I422 or Av1PixelLayout.I444 or Av1PixelLayout.I400
           && t_speedPlus1 >= 1 && Av1ObuWriter.PlainStillAnyTiling;

    /// <summary>Encodes one frame. Planes hold samples 0..(1 &lt;&lt; bitDepth) - 1 (ushort, the earlier encoder's plane type); chroma planes
    /// are ((w + ssX) &gt;&gt; ssX) x ((h + ssY) &gt;&gt; ssY); u / v are ignored for 4:0:0. qIdx 0 codes losslessly.</summary>
    internal static (byte[] SeqObu, byte[] FrameObu) Encode(ReadOnlySpan<ushort> y, ReadOnlySpan<ushort> u, ReadOnlySpan<ushort> v,
        int width, int height, Av1PixelLayout layout, int qIdx, Av1ObuWriter.Av1ColorDesc? color, int bitDepth = 8)
    {
        if (t_padTo is { } pad && (pad.Width > width || pad.Height > height))
        {
            // libavif avifImageCopyAndPad: a smaller right / bottom grid cell coded at the first cell's size, its planes
            // extended with their last column / row
            bool m = layout == Av1PixelLayout.I400;
            int sx = layout == Av1PixelLayout.I444 ? 0 : 1, sy = layout == Av1PixelLayout.I420 ? 1 : 0;
            ushort[] Pad(ReadOnlySpan<ushort> src, int w0, int h0, int w1, int h1)
            {
                var d = new ushort[w1 * h1];
                for (int r = 0; r < h1; r++)
                {
                    var row = d.AsSpan(r * w1, w1);
                    if (r < h0)
                    {
                        src.Slice(r * w0, w0).CopyTo(row);
                        row.Slice(w0).Fill(src[r * w0 + w0 - 1]);
                    }
                    else d.AsSpan((h0 - 1) * w1, w1).CopyTo(row);
                }
                return d;
            }
            int pw = Math.Max(pad.Width, width), ph = Math.Max(pad.Height, height);
            var py = Pad(y, width, height, pw, ph);
            ushort[]? pu = null, pv = null;
            if (!m)
            {
                int cw0 = (width + sx) >> sx, ch0 = (height + sy) >> sy, cw1 = (pw + sx) >> sx, ch1 = (ph + sy) >> sy;
                pu = Pad(u, cw0, ch0, cw1, ch1);
                pv = Pad(v, cw0, ch0, cw1, ch1);
            }
            var prev = t_padTo;
            t_padTo = null;
            try { return Encode(py, pu, pv, pw, ph, layout, qIdx, color, bitDepth); }
            finally { t_padTo = prev; }
        }
        bool mono = layout == Av1PixelLayout.I400;
        int ssX = layout == Av1PixelLayout.I444 ? 0 : 1, ssY = layout == Av1PixelLayout.I420 || mono ? 1 : 0;
        int cw = (width + ssX) >> ssX, ch = (height + ssY) >> ssY;
        bool hbd = bitDepth > 8;
        var planes = hbd ? null : mono ? new[] { Narrow(y, width * height) } : new[] { Narrow(y, width * height), Narrow(u, cw * ch), Narrow(v, cw * ch) };
        var planes16 = !hbd ? null : mono ? new[] { y[..(width * height)].ToArray() }
            : new[] { y[..(width * height)].ToArray(), u[..(cw * ch)].ToArray(), v[..(cw * ch)].ToArray() };
        var input = new AomEncodeInput
        {
            Width = width, Height = height, SsX = ssX, SsY = ssY, Monochrome = mono, Planes = planes!, Planes16 = planes16,
            BitDepth = bitDepth,
            // libavif: AV1E_SET_ENABLE_RESTORATION 0 for 12-bit input (crbug.com/aomedia/42302587)
            EnableRestoration = bitDepth != 12,
            Strides = mono ? new[] { width } : new[] { width, cw, cw }, BaseQindex = qIdx, Speed = Speed,
            // libavif: maxThreads > 1 -> cfg.g_threads = min(maxThreads, 64) (row-MT, libaom's default)
            Threads = Math.Min(Av1StillImageEncoder.ThreadCount, 64),
            Tune = color != null ? t_tune : AomTune.Psnr, Sharpness = t_sharpness, EnableCdef = t_enableCdef,
            // libavif: AV1E_SET_TILE_COLUMNS / AV1E_SET_TILE_ROWS (explicit --tilecolslog2 / --tilerowslog2 or autotiling)
            TileColumns = Av1ObuWriter.TileLog2Request.Cols, TileRows = Av1ObuWriter.TileLog2Request.Rows,
        };
        var (cpi, x) = AomEncoder.EncodeFrame(input);
        AomEncoder.RunPostFilter(cpi, x, applyRestoration: false);
        var c = color ?? Av1ObuWriter.Av1ColorDesc.Legacy;
        var seqCfg = new AomSequenceConfig
        {
            ColorPrimaries = c.Primaries, TransferCharacteristics = c.Transfer, MatrixCoefficients = c.Matrix,
            ColorRange = c.FullRange ? 1 : 0, FilmGrain = Av1ObuWriter.ActiveFilmGrain, ChromaSamplePosition = layout == Av1PixelLayout.I420 ? Av1ObuWriter.ChromaSamplePosition : 0,
        };
        var packet = AomBitstream.PackFrame(cpi, seqCfg);
        AomEncoder.ReleaseFrame(cpi);
        return SplitPacket(packet);
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
