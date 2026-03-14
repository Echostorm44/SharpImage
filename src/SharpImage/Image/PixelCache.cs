using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Image;

/// <summary>
/// Memory-backed pixel cache. Stores pixel data as a flat ushort array organized by scanline. Each scanline contains
/// (Columns * NumberOfChannels) quantum values. Uses ArrayPool for large allocations to minimize GC pressure.
/// </summary>
public class PixelCache : IDisposable
{
    private ushort[]? pixelData;
    private readonly long columns;
    private readonly long rows;
    private readonly int channelsPerPixel;
    private readonly long strideInQuantums; // elements per row
    private readonly bool isPooled;
    private bool disposed;

    /// <summary>
    /// Width in pixels.
    /// </summary>
    public long Columns => columns;

    /// <summary>
    /// Height in pixels.
    /// </summary>
    public long Rows => rows;

    /// <summary>
    /// Number of channels per pixel.
    /// </summary>
    public int ChannelsPerPixel => channelsPerPixel;

    /// <summary>
    /// Number of quantum values per scanline.
    /// </summary>
    public long Stride => strideInQuantums;

    /// <summary>
    /// Creates a pixel cache for the given dimensions and channel count.
    /// </summary>
    public PixelCache(long columns, long rows, int channelsPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelsPerPixel);

        this.columns = columns;
        this.rows = rows;
        this.channelsPerPixel = channelsPerPixel;
        strideInQuantums = columns * channelsPerPixel;

        long totalQuantums = strideInQuantums * rows;

        // Use ArrayPool for buffers over 1KB (512 ushorts)
        if (totalQuantums > 512 && totalQuantums <= int.MaxValue)
        {
            pixelData = ArrayPool<ushort>.Shared.Rent((int)totalQuantums);
            isPooled = true;
            // Clear only the portion we'll use
            pixelData.AsSpan(0, (int)totalQuantums).Clear();
        }
        else if (totalQuantums <= int.MaxValue)
        {
            pixelData = new ushort[(int)totalQuantums];
            isPooled = false;
        }
        else
        {
            throw new OutOfMemoryException(
                $"Image dimensions {columns}x{rows} with {channelsPerPixel} channels " +
                $"requires {totalQuantums} quantum values, exceeding maximum array size.");
        }
    }

    /// <summary>
    /// Gets a read-only span of pixel data for the specified row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ushort> GetRow(long y)
    {
        ValidateRow(y);
        int offset = (int)(y * strideInQuantums);
        return pixelData.AsSpan(offset, (int)strideInQuantums);
    }

    /// <summary>
    /// Gets a writable span of pixel data for the specified row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<ushort> GetRowForWrite(long y)
    {
        ValidateRow(y);
        int offset = (int)(y * strideInQuantums);
        return pixelData.AsSpan(offset, (int)strideInQuantums);
    }

    /// <summary>
    /// Gets a read-only span of the entire pixel data buffer.
    /// </summary>
    public ReadOnlySpan<ushort> GetAllPixels()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return pixelData.AsSpan(0, (int)(strideInQuantums * rows));
    }

    /// <summary>
    /// Gets a writable span of the entire pixel data buffer.
    /// </summary>
    public Span<ushort> GetAllPixelsForWrite()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return pixelData.AsSpan(0, (int)(strideInQuantums * rows));
    }

    /// <summary>
    /// Fills the entire cache with the specified value.
    /// </summary>
    public void Fill(ushort value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pixelData.AsSpan(0, (int)(strideInQuantums * rows)).Fill(value);
    }

    /// <summary>
    /// Clears the entire cache to zero.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pixelData.AsSpan(0, (int)(strideInQuantums * rows)).Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateRow(long y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((ulong)y >= (ulong)rows)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, $"Row must be in range [0, {rows - 1}]");
        }

        if (pixelData is null)
        {
            throw new InvalidOperationException("Pixel cache has been disposed");
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            if (pixelData is not null && isPooled)
            {
                ArrayPool<ushort>.Shared.Return(pixelData);
            }

            pixelData = null;
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~PixelCache()
    {
        Dispose();
    }
}
