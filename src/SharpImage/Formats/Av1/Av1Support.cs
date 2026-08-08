// Support types for the vendored AV1 decoder.
//
// The AV1 intra decoder in this folder is vendored from MediaKernel
// (F:\Code\MediaKernel\src\MediaKernel.Core\Codecs\AV1), where it decodes AV1 keyframes
// pixel-identically to dav1d. It is used here to decode AVIF still images (AVIF = an AV1
// intra frame in a HEIF/ISOBMFF container). These few types replace the small MediaKernel
// runtime surface the decoder needs, so the codec builds standalone inside SharpImage with
// no external dependencies.
using System;
using System.Buffers;
using System.Diagnostics;

namespace SharpImage.Formats.Av1;

/// <summary>
/// No-op sink for the decoder's diagnostic prints (vendored from MediaKernel, where they
/// were compared against dav1d traces). Marked [Conditional] so the compiler strips every
/// call — and the string it would build — unless AV1_DEBUG is defined.
/// </summary>
internal static class AvDbg
{
    [Conditional("AV1_DEBUG")] public static void W() { }
    [Conditional("AV1_DEBUG")] public static void W(string s) { }
    [Conditional("AV1_DEBUG")] public static void W(object? o) { }
}

/// <summary>Pixel layout of a <see cref="DecodedVideoFrame"/>.</summary>
internal enum PixelFormat
{
    Unknown,
    Yuv420P,
    Yuv422P,
    Yuv444P,
    Yuv420P10,
    Yuv422P10,
    Yuv444P10,
    Yuv420P12,
    Yuv422P12,
    Yuv444P12,
}

/// <summary>
/// A decoded planar YUV frame produced by the AV1 decoder. Holds a pooled backing buffer;
/// dispose to return it. Minimal replacement for MediaKernel's DecodedVideoFrame.
/// </summary>
internal sealed class DecodedVideoFrame : IDisposable
{
    private readonly byte[] rentedBuffer;
    private bool disposed;

    public int Width { get; }
    public int Height { get; }
    public PixelFormat Format { get; }
    public long PresentationTimeTicks { get; }

    public ReadOnlyMemory<byte> YPlane { get; }
    public ReadOnlyMemory<byte> UPlane { get; }
    public ReadOnlyMemory<byte> VPlane { get; }

    public int YStride { get; }
    public int UStride { get; }
    public int VStride { get; }

    public DecodedVideoFrame(
        int width, int height, PixelFormat format, long pts,
        byte[] buffer,
        int yOffset, int yStride,
        int uOffset, int uStride,
        int vOffset, int vStride)
    {
        Width = width;
        Height = height;
        Format = format;
        PresentationTimeTicks = pts;
        rentedBuffer = buffer;
        YStride = yStride;
        UStride = uStride;
        VStride = vStride;

        bool is420 = format is PixelFormat.Yuv420P or PixelFormat.Yuv420P10 or PixelFormat.Yuv420P12;
        int uvHeight = is420 ? (height + 1) / 2 : height;
        YPlane = new ReadOnlyMemory<byte>(buffer, yOffset, yStride * height);
        UPlane = new ReadOnlyMemory<byte>(buffer, uOffset, uStride * uvHeight);
        VPlane = new ReadOnlyMemory<byte>(buffer, vOffset, vStride * uvHeight);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<byte>.Shared.Return(rentedBuffer);
    }
}
