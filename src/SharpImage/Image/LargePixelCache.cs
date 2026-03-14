using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace SharpImage.Image;

/// <summary>
/// Memory-mapped pixel cache for very large images (over ~256MB). Uses OS virtual memory to avoid GC pressure entirely.
/// Falls back to standard PixelCache for images under the threshold.
/// </summary>
public sealed class LargePixelCache : IDisposable
{
    /// <summary>
    /// Threshold in bytes above which memory-mapped files are used. Default 256MB — images below this use ArrayPool-
    /// backed PixelCache.
    /// </summary>
    public const long MemoryMapThresholdBytes = 256 * 1024 * 1024;

    private readonly MemoryMappedFile mappedFile;
    private readonly MemoryMappedViewAccessor accessor;
    private readonly unsafe byte* basePointer;
    private readonly long totalBytes;
    private readonly long columns;
    private readonly long rows;
    private readonly int channelsPerPixel;
    private readonly long strideInBytes; // bytes per row
    private bool disposed;

    public long Columns => columns;
    public long Rows => rows;
    public int ChannelsPerPixel => channelsPerPixel;
    public long Stride => columns * channelsPerPixel;

    /// <summary>
    /// Determines whether the given image dimensions should use memory-mapped storage.
    /// </summary>
    public static bool ShouldUseMemoryMap(long columns, long rows, int channels)
    {
        return columns * rows * channels * sizeof(ushort) > MemoryMapThresholdBytes;
    }

    public LargePixelCache(long columns, long rows, int channelsPerPixel)
    {
        this.columns = columns;
        this.rows = rows;
        this.channelsPerPixel = channelsPerPixel;
        strideInBytes = columns * channelsPerPixel * sizeof(ushort);
        totalBytes = strideInBytes * rows;

        mappedFile = MemoryMappedFile.CreateNew(null, totalBytes, MemoryMappedFileAccess.ReadWrite);
        try
        {
            accessor = mappedFile.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            mappedFile.Dispose();
            throw;
        }

        unsafe
        {
            byte* ptr = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            basePointer = ptr;
        }
    }

    /// <summary>
    /// Gets a read-only span for the specified row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ReadOnlySpan<ushort> GetRow(long y)
    {
        ValidateRow(y);
        long byteOffset = y * strideInBytes;
        return new ReadOnlySpan<ushort>(basePointer + byteOffset, (int)(columns * channelsPerPixel));
    }

    /// <summary>
    /// Gets a writable span for the specified row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<ushort> GetRowForWrite(long y)
    {
        ValidateRow(y);
        long byteOffset = y * strideInBytes;
        return new Span<ushort>(basePointer + byteOffset, (int)(columns * channelsPerPixel));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateRow(long y)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((ulong)y >= (ulong)rows)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            mappedFile.Dispose();
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~LargePixelCache() => Dispose();
}
