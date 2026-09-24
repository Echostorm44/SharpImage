// AVIF Sample Transform ('sato', AVIF 1.2 section 4.2.3): a derived image item whose samples are computed per plane from
// its input image items by a postfix expression — libavif uses it for bit-depth extension (avifenc -d 8,8 / 12,4 /
// 12,8: a lossless or lossy base item plus a hidden item with the remaining bits, combined into a 16-bit image). The
// 'sato' item is found and evaluated as libavif does when sample transforms are decoded (AVIF_IMAGE_CONTENT_SAMPLE_TRANSFORMS,
// avifdec --sato; libavif's default decodes only the base item): the first 'sato' item preferred over the primary in an
// 'altr' group, 32-bit expressions, output clamped to its pixi depth. We always decode it — the full-precision image.
using SharpImage.Core;
using SharpImage.Image;
using System.Buffers;
using System.Buffers.Binary;

namespace SharpImage.Formats;

public static partial class HeifCoder
{
    private enum SatoOp : byte
    {
        Constant = 0, Input = 1,
        Negation = 64, Absolute = 65, Not = 66, Bsr = 67,
        Sum = 128, Difference = 129, Product = 130, Quotient = 131, And = 132, Or = 133, Xor = 134, Pow = 135, Min = 136, Max = 137,
    }

    private readonly record struct SatoToken(SatoOp Op, int Constant, int Input);

    // libavif avifDecoderDataFindSampleTransformImageItem + avifParseSampleTransformImageBox. Null: no usable 'sato'.
    private static (int Id, List<SatoToken> Expr, List<int> Inputs, int Depth)? FindSampleTransform(HeifContainer c, int pid)
    {
        foreach (var item in c.Items.Values.OrderBy(i => i.Id))
        {
            if (item.Type != "sato" || item.Id == pid || c.ItemData(item.Id) is not { Length: > 0 } p) continue;
            if (!IsPreferredAlternative(c, (uint)item.Id, (uint)pid)) continue;
            var inputs = c.ReferencesFrom(item.Id, "dimg");
            if (inputs.Count is 0 or > 32) throw new InvalidDataException("Box[sato] needs 1 to 32 input image items.");
            int version = p[0] >> 6, bitDepth = p[0] & 3;
            if (version != 0 || bitDepth != 2) throw new NotSupportedException("Only version 0, 32-bit Sample Transform expressions are supported.");
            if (p.Length < 2 || p[1] == 0) throw new InvalidDataException("Box[sato] has no tokens.");
            var expr = new List<SatoToken>(p[1]);
            int pos = 2;
            for (int t = 0; t < p[1]; t++)
            {
                if (pos >= p.Length) throw new InvalidDataException("Truncated Box[sato].");
                byte v = p[pos++];
                if (v == 0)
                {
                    if (pos + 4 > p.Length) throw new InvalidDataException("Truncated Box[sato].");
                    expr.Add(new SatoToken(SatoOp.Constant, BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(pos)), 0));
                    pos += 4;
                }
                else if (v <= 32) expr.Add(new SatoToken(SatoOp.Input, 0, v));
                else if (v is >= 64 and <= 67 or >= 128 and <= 137) expr.Add(new SatoToken((SatoOp)v, 0, 0));
                else throw new InvalidDataException($"Box[sato] has a reserved token {v}.");
            }
            if (pos != p.Length) throw new InvalidDataException("Box[sato] has trailing bytes.");
            // avifSampleTransformExpressionIsValid.
            int stack = 0;
            foreach (var tk in expr)
            {
                if (tk.Op == SatoOp.Input && (tk.Input == 0 || tk.Input > inputs.Count)) throw new InvalidDataException("Box[sato] references a missing input.");
                if ((byte)tk.Op < 64) stack++;
                else if ((byte)tk.Op < 128) { if (stack < 1) throw new InvalidDataException("Invalid Box[sato] expression."); }
                else { if (stack < 2) throw new InvalidDataException("Invalid Box[sato] expression."); stack--; }
            }
            if (stack != 1) throw new InvalidDataException("Invalid Box[sato] expression.");
            int depth = c.Property(item.Id, "pixi") is { Len: >= 6 } px ? c.Data[px.Off + 5] : throw new InvalidDataException("Box[sato] item lacks pixi.");
            if (depth is < 1 or > 16) throw new NotSupportedException($"Sample Transform depth {depth} is not supported.");
            return (item.Id, expr, inputs, depth);
        }
        return null;
    }

    // One input image item's native samples: Y, U, V (null for 4:0:0) plane arrays with their widths.
    private sealed record ItemPlanes(int Width, int Height, int Depth, Av1.PixelFormat Format, bool Mono, ushort[][] Planes, int[] Widths, int[] Heights,
        bool FullRange = true, int ChromaSamplePosition = 0);

    private static ItemPlanes DecodeItemPlanes(HeifContainer c, int id)
    {
        var (tiles, rows, cols, outW, outH, codec) = ResolveImageTiles(c, id);
        if (codec != "av01") throw new NotSupportedException("Sample Transform inputs must be AV1 items.");
        var frames = new List<Av1.DecodedVideoFrame>();
        try
        {
            Av1.Av1Decoder? first = null;
            foreach (int t in tiles)
            {
                byte[] coded = c.ItemData(t) ?? throw new InvalidDataException($"Item {t} has no data.");
                frames.Add(DecodeAv1Item(c, t, coded, out var dec, $"Sample Transform input {t}"));
                first ??= dec;
            }
            var f0 = frames[0];
            if (outW <= 0 || outH <= 0) (outW, outH) = (f0.Width * cols, f0.Height * rows);
            bool mono = first!.Monochrome;
            int ssX = f0.Format is Av1.PixelFormat.Yuv444P or Av1.PixelFormat.Yuv444P10 or Av1.PixelFormat.Yuv444P12 ? 0 : 1;
            int ssY = f0.Format is Av1.PixelFormat.Yuv420P or Av1.PixelFormat.Yuv420P10 or Av1.PixelFormat.Yuv420P12 ? 1 : 0;
            int n = mono ? 1 : 3;
            var planes = new ushort[n][];
            var widths = new int[n];
            var heights = new int[n];
            for (int pl = 0; pl < n; pl++)
            {
                int sx = pl == 0 ? 0 : ssX, sy = pl == 0 ? 0 : ssY;
                widths[pl] = (outW + sx) >> sx;
                heights[pl] = (outH + sy) >> sy;
                planes[pl] = new ushort[widths[pl] * heights[pl]];
            }
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                int x0 = (i % cols) * f0.Width, y0 = (i / cols) * f0.Height;
                for (int pl = 0; pl < n; pl++)
                {
                    int sx = pl == 0 ? 0 : ssX, sy = pl == 0 ? 0 : ssY;
                    int fw = (f.Width + sx) >> sx, fh = (f.Height + sy) >> sy;
                    int px0 = x0 >> sx, py0 = y0 >> sy;
                    int cw = Math.Min(fw, widths[pl] - px0), chh = Math.Min(fh, heights[pl] - py0);
                    int stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
                    for (int y = 0; y < chh; y++)
                        for (int x = 0; x < cw; x++)
                            planes[pl][(py0 + y) * widths[pl] + px0 + x] = f.BitDepth > 8
                                ? (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span[y * stride + x]
                                : (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span[y * stride + x];
                }
            }
            if (!first.FullColorRange && ReferencesAlpha(c, id))
            {
                // Limited-range alpha is expanded like libavif (only alpha items can be limited here).
                int max = (1 << f0.BitDepth) - 1, lo = 16 << (f0.BitDepth - 8), hi = 235 << (f0.BitDepth - 8);
                var a = planes[0];
                for (int k = 0; k < a.Length; k++) a[k] = (ushort)Math.Clamp(((a[k] - lo) * max + (hi - lo) / 2) / (hi - lo), 0, max);
            }
            return new ItemPlanes(outW, outH, f0.BitDepth, f0.Format, mono, planes, widths, heights, first.FullColorRange,
                first.ChromaSamplePosition);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
    }

    private static bool ReferencesAlpha(HeifContainer c, int id)
    {
        foreach (var (t, from, _) in c.References) if (t == "auxl" && from == id) return true;
        return false;
    }

    // avifImageApplyExpression32b over one plane of every input.
    private static ushort[] ApplySatoExpression(List<SatoToken> expr, ushort[][] inputs, int count, int depth)
    {
        var dst = new ushort[count];
        int max = (1 << depth) - 1;
        var stack = new int[expr.Count / 2 + 1];
        static int Clamp32(long v) => v <= int.MinValue ? int.MinValue : v >= int.MaxValue ? int.MaxValue : (int)v;
        for (int i = 0; i < count; i++)
        {
            int sp = 0;
            foreach (var tk in expr)
            {
                switch (tk.Op)
                {
                    case SatoOp.Constant: stack[sp++] = tk.Constant; break;
                    case SatoOp.Input: stack[sp++] = inputs[tk.Input - 1][i]; break;
                    case SatoOp.Negation: stack[sp - 1] = Clamp32(-(long)stack[sp - 1]); break;
                    case SatoOp.Absolute: stack[sp - 1] = stack[sp - 1] >= 0 ? stack[sp - 1] : Clamp32(-(long)stack[sp - 1]); break;
                    case SatoOp.Not: stack[sp - 1] = ~stack[sp - 1]; break;
                    case SatoOp.Bsr:
                    {
                        int o = stack[sp - 1];
                        if (o <= 0) { stack[sp - 1] = 0; break; }
                        int log2 = 0;
                        for (o >>= 1; o != 0; log2++) o >>= 1;
                        stack[sp - 1] = log2;
                        break;
                    }
                    default:
                    {
                        int l = stack[sp - 2], r = stack[sp - 1];
                        stack[sp - 2] = tk.Op switch
                        {
                            SatoOp.Sum => Clamp32((long)l + r),
                            SatoOp.Difference => Clamp32((long)l - r),
                            SatoOp.Product => Clamp32((long)l * r),
                            SatoOp.Quotient => r == 0 ? l : Clamp32((long)l / r),
                            SatoOp.And => l & r,
                            SatoOp.Or => l | r,
                            SatoOp.Xor => l ^ r,
                            SatoOp.Pow => SatoPow(l, r),
                            SatoOp.Min => l <= r ? l : r,
                            _ => l <= r ? r : l,
                        };
                        sp--;
                        break;
                    }
                }
            }
            dst[i] = (ushort)Math.Clamp(stack[0], 0, max);
        }
        return dst;
    }

    private static int SatoPow(int l, int r)
    {
        if (l == 0 || l == 1) return l;
        if (l == -1) return r % 2 == 0 ? 1 : -1;
        if (r == 0) return 1;
        if (r == 1) return l;
        if (r < 0) return 0;
        long result = l;
        for (int i = 1; i < r; i++)
        {
            result *= l;
            if (result < int.MinValue || result > int.MaxValue) return l > 0 || r % 2 == 0 ? int.MaxValue : int.MinValue;
        }
        return (int)result;
    }

    // avifenc -d D,Dextension (libavif avifEncoderCreateBitDepthExtensionItems / avifEncoderCreateSatoImage): RGB -> 16-bit
    // YUV with libavif's float path, split into a base (8 or 12 bits) and a hidden 8-bit item, combined back by 'sato'.
    private static byte[] EncodeAvifBitDepthExtension(ImageFrame image, AvifEncodeOptions options)
    {
        if (options.Grid != null || options.Layers is { Count: > 0 } || options.Progressive || options.GainMap != null
            || options.FilmGrain != null || options.DenoiseNoiseLevel > 0)
            throw new NotSupportedException("BitDepthExtension cannot be combined with grids, layers, progressive, gain maps or film grain.");
        var recipe = options.BitDepthExtension;
        int w = (int)image.Columns, h = (int)image.Rows;
        const int bd = 16;
        var color = ResolveAvifColor(image, options, 12);
        if (options.Lossless && options.MatrixCoefficients == null) color = color with { Matrix = 0 };
        if (options.ChromaSubsampling == AvifChromaSubsampling.Yuv400 && color.Matrix == 0) color = color with { Matrix = 6 };
        var layout = options.ChromaSubsampling switch
        {
            AvifChromaSubsampling.Yuv422 => Av1.Av1PixelLayout.I422,
            AvifChromaSubsampling.Yuv444 => Av1.Av1PixelLayout.I444,
            AvifChromaSubsampling.Yuv420 => Av1.Av1PixelLayout.I420,
            AvifChromaSubsampling.Yuv400 => Av1.Av1PixelLayout.I400,
            _ => color.Matrix == 0 || options.Lossless ? Av1.Av1PixelLayout.I444 : Av1.Av1PixelLayout.I420,
        };
        if (color.Matrix == 0 && layout != Av1.Av1PixelLayout.I444)
            throw new ArgumentException("The identity matrix (MatrixCoefficients 0) requires 4:4:4 chroma.", nameof(options));

        ReadRgbPlanes(image, bd, out var r, out var g, out var b, out var alpha16, out bool colour, out bool nonOpaque);
        int srcDepth = image.Depth is >= 1 and <= 16 ? image.Depth : 16;
        bool mono = layout == Av1.Av1PixelLayout.I400 || (!colour && color.Matrix is not (0 or 16 or 17));
        var coded = mono ? Av1.Av1PixelLayout.I400 : layout;
        ushort[][] planes;
        if (mono)
            planes = [colour ? MonoLumaLibavif(r, g, b, w, h, bd, color, srcDepth) : GreyLuma(r, w, h, bd, color)];
        else
        {
            RgbToYuvAvifFloat(r, g, b, w, h, bd, layout, color, out var y, out var u, out var v, srcDepth);
            planes = [y, u, v];
        }
        ushort[]? alpha = image.HasAlpha && nonOpaque ? alpha16 : null;

        // Qualities: the base of 8+8 / 12+4 is always lossless (libavif), the hidden items follow Quality / QualityAlpha.
        var (qIdxQ, aQIdxQ, qualityLossless) = QualityQIndices(options, color);
        bool lossless = options.Lossless || qualityLossless;
        int qIdx = qIdxQ ?? Math.Clamp((int)Math.Round(Math.Clamp(options.Qp, 0, 51) * (255.0 / 51.0)), 4, 255);
        int aQIdx = aQIdxQ ?? (lossless ? 0 : Math.Clamp(qIdx / 2, 4, 255));
        int baseBd = recipe == AvifBitDepthExtension.Bits8Plus8 ? 8 : 12;
        bool baseLossless = recipe != AvifBitDepthExtension.Bits12Plus8Overlap4 || lossless;
        var cDesc = mono && color.Matrix is 16 or 17 ? color with { Matrix = 0 } : color;

        (byte[] Seq, byte[] Frame) Code(ushort[][] p, int depth, bool isLossless, int q, bool isAlpha)
        {
            if (isAlpha)
                return isLossless
                    ? Av1.Av1StillImageEncoder.BuildLosslessObus(p[0], default, default, true, w, h, depth, Av1.Av1PixelLayout.I400, null)
                    : Av1.Av1StillImageEncoder.BuildMonochromeObus(p[0], w, h, q, depth);
            if (isLossless)
                return Av1.Av1StillImageEncoder.BuildLosslessObus(p[0], mono ? default : p[1], mono ? default : p[2], mono, w, h, depth, coded, cDesc);
            return mono ? Av1.Av1StillImageEncoder.BuildMonochromeObus(p[0], w, h, q, depth, color)
                : Av1.Av1StillImageEncoder.BuildColorObus(p[0], p[1], p[2], w, h, q, depth, layout, color);
        }

        (byte[] Base, byte[] Hidden) Split(ushort[][] p, bool isAlpha)
        {
            bool hiddenLossless = isAlpha ? aQIdx == 0 : lossless;
            ushort[][] basePl = p.Select(pl => pl.Select(v => (ushort)(baseBd == 8 ? v / 256 : v / 16)).ToArray()).ToArray();
            var baseObus = Code(basePl, baseBd, baseLossless, isAlpha ? aQIdx : qIdx, isAlpha);
            ushort[][] hidden;
            switch (recipe)
            {
                case AvifBitDepthExtension.Bits8Plus8:
                    hidden = p.Select(pl => pl.Select(v => (ushort)(v & 255)).ToArray()).ToArray();
                    break;
                case AvifBitDepthExtension.Bits12Plus4:
                    hidden = p.Select(pl => pl.Select(v => (ushort)((v & 15) * 16 + (hiddenLossless ? 0 : 7))).ToArray()).ToArray();
                    break;
                default:
                {
                    // decoded = main * 16 + hidden - 128, so hidden = clamp_8b(original - main * 16 + 128) with the
                    // DECODED base (what readers will reconstruct from).
                    var decoded = DecodeObuPlanes(baseObus.Seq, baseObus.Frame, p.Length);
                    hidden = new ushort[p.Length][];
                    for (int k = 0; k < p.Length; k++)
                    {
                        hidden[k] = new ushort[p[k].Length];
                        for (int i = 0; i < p[k].Length; i++)
                            hidden[k][i] = (ushort)Math.Clamp(p[k][i] - decoded[k][i] * 16 + 128, 0, 255);
                    }
                    break;
                }
            }
            var hiddenObus = Code(hidden, 8, hiddenLossless, isAlpha ? aQIdx : qIdx, isAlpha);
            return ([.. baseObus.Seq, .. baseObus.Frame], [.. hiddenObus.Seq, .. hiddenObus.Frame]);
        }

        var (cBase, cHidden) = Split(planes, isAlpha: false);
        byte[]? aBase = null, aHidden = null;
        if (alpha != null) (aBase, aHidden) = Split([alpha], isAlpha: true);

        var extras = AvifExtras(image, options);
        extras.Premultiplied = false;
        extras.SampleTransform = new Av1.AvifSampleTransformItems
        {
            Payload = SatoPayload(recipe),
            Depth = 16,
            HiddenColor = cHidden,
            HiddenDepth = 8,
            HiddenColorAv1CBox = Av1.Av1AvifWriter.Box("av1C", Av1.Av1AvifWriter.BuildAv1C(coded, 8, w, h)),
            HiddenAlpha = aHidden,
            HiddenAlphaAv1CBox = Av1.Av1AvifWriter.Box("av1C", Av1.Av1AvifWriter.BuildAv1C(Av1.Av1PixelLayout.I400, 8, w, h)),
        };
        return aBase != null
            ? Av1.Av1AvifWriter.BuildAvifWithAlpha(cBase, [], aBase, [], w, h, mono, baseBd, coded, color, extras)
            : Av1.Av1AvifWriter.BuildAvif(cBase, [], w, h, mono, baseBd, coded, color, extras);
    }

    // The planes of a coded temporal unit, decoded by our decoder (native samples).
    private static ushort[][] DecodeObuPlanes(byte[] seq, byte[] frame, int count)
    {
        var dec = new Av1.Av1Decoder();
        using var f = dec.Decode([.. seq, .. frame], 0, isKeyframe: true) ?? throw new InvalidOperationException("Base item decode failed.");
        int ssX = f.Format is Av1.PixelFormat.Yuv444P ? 0 : 1, ssY = f.Format is Av1.PixelFormat.Yuv420P ? 1 : 0;
        var res = new ushort[count][];
        for (int pl = 0; pl < count; pl++)
        {
            int sx = pl == 0 ? 0 : ssX, sy = pl == 0 ? 0 : ssY;
            int pw = (f.Width + sx) >> sx, ph = (f.Height + sy) >> sy, stride = pl == 0 ? f.YStride : pl == 1 ? f.UStride : f.VStride;
            res[pl] = new ushort[pw * ph];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                    res[pl][y * pw + x] = f.BitDepth > 8
                        ? (pl == 0 ? f.YPlane16 : pl == 1 ? f.UPlane16 : f.VPlane16).Span[y * stride + x]
                        : (pl == 0 ? f.YPlane : pl == 1 ? f.UPlane : f.VPlane).Span[y * stride + x];
        }
        return res;
    }

    // SampleTransform box contents for a recipe (libavif avifSampleTransformRecipeToExpression): version 0, 32-bit.
    private static byte[] SatoPayload(AvifBitDepthExtension recipe)
    {
        var t = new List<byte> { 0x02 };
        void C(int v) { t.Add(0); t.Add((byte)(v >> 24)); t.Add((byte)(v >> 16)); t.Add((byte)(v >> 8)); t.Add((byte)v); }
        void I(int i) => t.Add((byte)i);
        void O(SatoOp op) => t.Add((byte)op);
        t.Add(0);   // token count, patched below
        switch (recipe)
        {
            case AvifBitDepthExtension.Bits8Plus8: C(256); I(1); O(SatoOp.Product); I(2); O(SatoOp.Or); t[1] = 5; break;
            case AvifBitDepthExtension.Bits12Plus4: C(16); I(1); O(SatoOp.Product); I(2); C(16); O(SatoOp.Quotient); O(SatoOp.Sum); t[1] = 7; break;
            default: C(16); I(1); O(SatoOp.Product); I(2); O(SatoOp.Sum); C(128); O(SatoOp.Difference); t[1] = 7; break;
        }
        return t.ToArray();
    }

    // Decodes the 'sato' item of the primary to a frame at the transform's depth (colour from the primary's CICP; alpha
    // combined from the inputs' alpha items with the same expression), or null when there is none.
    private static ImageFrame? TryDecodeSampleTransform(HeifContainer c, int pid)
    {
        if (FindSampleTransform(c, pid) is not { } sato) return null;
        var inputs = sato.Inputs.Select(id => DecodeItemPlanes(c, id)).ToList();
        var i0 = inputs[0];
        foreach (var inp in inputs)
            if (inp.Width != i0.Width || inp.Height != i0.Height || inp.Mono != i0.Mono || inp.Planes.Length != i0.Planes.Length
                || !inp.Widths.SequenceEqual(i0.Widths) || !inp.Heights.SequenceEqual(i0.Heights))
                throw new InvalidDataException("Box[sato] input items differ in size or chroma format.");
        int n = i0.Planes.Length;
        var outPlanes = new ushort[n][];
        for (int pl = 0; pl < n; pl++)
            outPlanes[pl] = ApplySatoExpression(sato.Expr, inputs.Select(x => x.Planes[pl]).ToArray(), i0.Widths[pl] * i0.Heights[pl], sato.Depth);

        // Alpha: every input's alpha item, combined the same way (libavif requires all inputs to have alpha or none).
        ushort[]? alpha = null;
        var alphaIds = sato.Inputs.Select(id => c.ReferencesTo(id, "auxl").Where(a => IsAlphaAux(c, a)).DefaultIfEmpty(-1).First()).ToList();
        if (alphaIds.All(a => a >= 0))
        {
            var aPlanes = alphaIds.Select(a => DecodeItemPlanes(c, a).Planes[0]).ToArray();
            alpha = ApplySatoExpression(sato.Expr, aPlanes, i0.Width * i0.Height, sato.Depth);
        }
        else if (alphaIds.Any(a => a >= 0)) throw new NotSupportedException("Box[sato] inputs with and without alpha.");

        // YUV -> RGB at the transform's depth (libavif converts the reconstructed 16-bit image).
        int w = i0.Width, h = i0.Height, depth = sato.Depth;
        bool mono = i0.Mono;
        int ssX = mono ? 1 : (i0.Widths[1] == w ? 0 : 1), ssY = mono ? 1 : (i0.Heights[1] == h ? 0 : 1);
        var format = mono || (ssX == 1 && ssY == 1) ? Av1.PixelFormat.Yuv420P : ssY == 0 && ssX == 1 ? Av1.PixelFormat.Yuv422P : Av1.PixelFormat.Yuv444P;
        int cw = mono ? (w + 1) / 2 : i0.Widths[1], chh = mono ? (h + 1) / 2 : i0.Heights[1];
        ushort[] u = mono ? new ushort[cw * chh] : outPlanes[1], v = mono ? new ushort[cw * chh] : outPlanes[2];
        byte[] buf = ArrayPool<byte>.Shared.Rent(w * h + 2 * cw * chh);
        using var frameYuv = new Av1.DecodedVideoFrame(w, h, format, 0, buf, 0, w, w * h, cw, w * h + cw * chh, cw)
        {
            BitDepth = depth, YPlane16 = outPlanes[0], UPlane16 = u, VPlane16 = v,
        };
        var nclx = Nclx(c, pid);
        var cicp = nclx ?? (2, 2, 2, true);
        var frame = new ImageFrame();
        frame.Initialize(w, h, ColorspaceType.SRGB, false);
        frame.Metadata.Cicp = new SharpImage.Metadata.CicpInfo(cicp.Item1, cicp.Item2, cicp.Item3, cicp.Item4);
        bool prem = alpha != null && alphaIds[0] >= 0 && c.ReferencesFrom(sato.Inputs[0], "prem").Contains(alphaIds[0]);
        ConvertYuvToRgbLibavif(frameYuv, frame, w, h, frame.NumberOfChannels, mono, cicp.Item3, cicp.Item4, cicp.Item1,
            mono ? 1 : ssX, mono ? 1 : ssY, prem ? alpha : null);
        if (alpha != null)
        {
            frame.SetAlpha(true);
            int nch = frame.NumberOfChannels;
            float srcMax = (1 << depth) - 1;
            for (int y = 0; y < h; y++)
            {
                var row = frame.GetPixelRowForWrite(y);
                for (int x = 0; x < w; x++)
                    row[x * nch + nch - 1] = depth == 16 ? alpha[y * w + x] : (ushort)Math.Clamp((int)(0.5f + (alpha[y * w + x] / srcMax) * 65535.0f), 0, 65535);
            }
        }
        frame.Depth = depth;
        return frame;
    }
}
