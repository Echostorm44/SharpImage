using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpImage.Core;

/// <summary>
/// Manages pooled memory for pixel buffers. Wraps ArrayPool to provide zero-allocation steady-state for pixel data
/// operations. Any buffer over 1KB rented frequently goes through the pool.
/// </summary>
public static class PixelBufferPool
{
    private static readonly ArrayPool<ushort> QuantumPool = ArrayPool<ushort>.Shared;
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    /// <summary>
    /// Rents a quantum (ushort) buffer of at least the specified length. The returned array may be larger than
    /// requested. Caller MUST return the buffer via ReturnQuantumBuffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort[] RentQuantumBuffer(int minimumLength)
    {
        return QuantumPool.Rent(minimumLength);
    }

    /// <summary>
    /// Returns a previously rented quantum buffer to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnQuantumBuffer(ushort[] buffer, bool clearArray = false)
    {
        QuantumPool.Return(buffer, clearArray);
    }

    /// <summary>
    /// Rents a byte buffer of at least the specified length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] RentByteBuffer(int minimumLength)
    {
        return BytePool.Rent(minimumLength);
    }

    /// <summary>
    /// Returns a previously rented byte buffer to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReturnByteBuffer(byte[] buffer, bool clearArray = false)
    {
        BytePool.Return(buffer, clearArray);
    }
}

/// <summary>
/// RAII wrapper for a pooled quantum buffer. Disposes automatically returns to pool. Use with 'using' statement or
/// declaration.
/// </summary>
public struct RentedQuantumBuffer : IDisposable
{
    private ushort[]? buffer;

    public readonly int Length { get; }

    public RentedQuantumBuffer(int minimumLength)
    {
        buffer = PixelBufferPool.RentQuantumBuffer(minimumLength);
        Length = minimumLength;
    }

    /// <summary>
    /// Gets the buffer as a span of the requested length (not the full rented array).
    /// </summary>
    public readonly Span<ushort> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => buffer.AsSpan(0, Length);
    }

    /// <summary>
    /// Gets the underlying array (may be larger than Length).
    /// </summary>
    public readonly ushort[] Array => buffer ?? throw new ObjectDisposedException(nameof(RentedQuantumBuffer));

    public void Dispose()
    {
        if (buffer is not null)
        {
            PixelBufferPool.ReturnQuantumBuffer(buffer);
            buffer = null;
        }
    }
}

/// <summary>
/// RAII wrapper for a pooled byte buffer. Disposes automatically returns to pool.
/// </summary>
public struct RentedByteBuffer : IDisposable
{
    private byte[]? buffer;

    public readonly int Length { get; }

    public RentedByteBuffer(int minimumLength)
    {
        buffer = PixelBufferPool.RentByteBuffer(minimumLength);
        Length = minimumLength;
    }

    public readonly Span<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => buffer.AsSpan(0, Length);
    }

    public readonly byte[] Array => buffer ?? throw new ObjectDisposedException(nameof(RentedByteBuffer));

    public void Dispose()
    {
        if (buffer is not null)
        {
            PixelBufferPool.ReturnByteBuffer(buffer);
            buffer = null;
        }
    }
}
