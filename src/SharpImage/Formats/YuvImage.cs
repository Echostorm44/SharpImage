namespace SharpImage.Formats;

/// <summary>
/// A planar Y'CbCr picture at its coded precision: what a Y4M file holds and what an AVIF item decodes to before any
/// RGB conversion (<see cref="HeifCoder.DecodeYuv(byte[], AvifDecodeOptions?)"/>, <see cref="Y4mCoder"/>). Samples
/// are stored one per <see cref="ushort"/> in [0, 2^<see cref="Depth"/>), rows packed at the plane width.
/// </summary>
public sealed class YuvImage
{
    /// <summary>Luma width and height in samples.</summary>
    public required int Width { get; init; }

    /// <inheritdoc cref="Width"/>
    public required int Height { get; init; }

    /// <summary>Bits per sample: 8, 10, 12 or 16.</summary>
    public required int Depth { get; init; }

    /// <summary>Chroma layout: <see cref="AvifChromaSubsampling.Yuv420"/>, <see cref="AvifChromaSubsampling.Yuv422"/>,
    /// <see cref="AvifChromaSubsampling.Yuv444"/> or <see cref="AvifChromaSubsampling.Yuv400"/> (no chroma planes).</summary>
    public required AvifChromaSubsampling Subsampling { get; init; }

    /// <summary>Full-range samples (else limited / "TV" range).</summary>
    public bool FullRange { get; init; }

    /// <summary>AV1 chroma_sample_position for 4:2:0: 0 unknown, 1 vertical (MPEG-2, left), 2 colocated (top-left).</summary>
    public int ChromaSamplePosition { get; init; }

    /// <summary>Luma plane (<see cref="Width"/> x <see cref="Height"/>).</summary>
    public required ushort[] Y { get; init; }

    /// <summary>Chroma planes (<see cref="ChromaWidth"/> x <see cref="ChromaHeight"/>), null for 4:0:0.</summary>
    public ushort[]? U { get; init; }

    /// <inheritdoc cref="U"/>
    public ushort[]? V { get; init; }

    /// <summary>Optional alpha plane (full resolution, same depth; Y4M C444alpha / an AVIF alpha item).</summary>
    public ushort[]? Alpha { get; init; }

    /// <summary>Chroma plane width.</summary>
    public int ChromaWidth => Subsampling is AvifChromaSubsampling.Yuv420 or AvifChromaSubsampling.Yuv422 ? (Width + 1) >> 1 : Width;

    /// <summary>Chroma plane height.</summary>
    public int ChromaHeight => Subsampling == AvifChromaSubsampling.Yuv420 ? (Height + 1) >> 1 : Height;
}
