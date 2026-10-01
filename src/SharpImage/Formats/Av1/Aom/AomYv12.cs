using System;

namespace SharpImage.Formats.Av1;

/// <summary>One plane of a YV12_BUFFER_CONFIG: 8-bit samples with a border on every side. Width/Height are the
/// allocated (mi-aligned) size libaom keeps (y_width = the frame width aligned to 8, chroma = that shifted by the
/// subsampling), CropWidth/CropHeight the visible size. The deblocker reads and writes the aligned area; the
/// restoration and every SSE use the crop.</summary>
internal sealed class AomYv12Plane
{
    public readonly byte[] Buf;
    /// <summary>High bit depth samples (Buf is null then).</summary>
    public readonly ushort[] Buf16 = null!;
    public readonly int Stride, Origin, Border;
    /// <summary>The frame's bit depth (8 for byte planes).</summary>
    public int BitDepth = 8;
    public readonly int Width, Height, CropWidth, CropHeight;

    public AomYv12Plane(int width, int height, int cropWidth, int cropHeight, int border, bool hbd = false)
    {
        Width = width;
        Height = height;
        CropWidth = cropWidth;
        CropHeight = cropHeight;
        Border = border;
        Stride = (width + 2 * border + 31) & ~31;
        Origin = border * Stride + border;
        if (hbd) Buf16 = AomBufferPool.Rent<ushort>(Stride * (height + 2 * border));
        else Buf = AomBufferPool.Rent<byte>(Stride * (height + 2 * border));
    }

    /// <summary>Gives the buffer back to AomBufferPool (the plane is unusable afterwards).</summary>
    public void Release() { AomBufferPool.Return(Buf); AomBufferPool.Return(Buf16); }

    /// <summary>Index of sample (x, y) (either may be negative, into the border).</summary>
    public int At(int x, int y) => Origin + y * Stride + x;

    /// <summary>Copies the whole buffer (borders included) from a plane of the same geometry.</summary>
    public void CopyFrom(AomYv12Plane src)
    {
        if (Buf16 != null)
        {
            if (src.Buf16.Length != Buf16.Length || src.Stride != Stride) throw new ArgumentException("plane geometry differs");
            Array.Copy(src.Buf16, Buf16, Buf16.Length);
            return;
        }
        if (src.Buf.Length != Buf.Length || src.Stride != Stride) throw new ArgumentException("plane geometry differs");
        Buffer.BlockCopy(src.Buf, 0, Buf, 0, Buf.Length);
    }

    /// <summary>Copies the Width x Height area (aom_yv12_copy_y/u/v with use_crop = 0).</summary>
    public void CopyAreaFrom(AomYv12Plane src)
    {
        if (Buf16 != null)
        {
            for (int r = 0; r < Height; r++) Array.Copy(src.Buf16, src.At(0, r), Buf16, At(0, r), Width);
            return;
        }
        for (int r = 0; r < Height; r++) Buffer.BlockCopy(src.Buf, src.At(0, r), Buf, At(0, r), Width);
    }

    /// <summary>Writes rows of w samples from a packed array (stride w) to (0, 0).</summary>
    public void Load(ReadOnlySpan<byte> data, int w, int h)
    {
        for (int r = 0; r < h; r++) data.Slice(r * w, w).CopyTo(Buf.AsSpan(At(0, r), w));
    }
}

/// <summary>YV12_BUFFER_CONFIG (8-bit): up to three planes, chroma subsampled by (SsX, SsY).</summary>
internal sealed class AomYv12
{
    public const int DefaultBorder = 64;

    public readonly AomYv12Plane[] Planes;
    public readonly int SsX, SsY, NumPlanes, BitDepth;
    public readonly int Width, Height;   // cm->width / cm->height (luma crop)

    public AomYv12(int width, int height, int ssX, int ssY, int numPlanes, int border = DefaultBorder, int bitDepth = 8)
    {
        Width = width;
        Height = height;
        SsX = ssX;
        SsY = ssY;
        NumPlanes = numPlanes;
        BitDepth = bitDepth;
        // aom_realloc_frame_buffer: aligned_width = (width + 7) & ~7, uv_width = aligned_width >> ss_x
        int aw = (width + 7) & ~7, ah = (height + 7) & ~7;
        Planes = new AomYv12Plane[numPlanes];
        Planes[0] = new AomYv12Plane(aw, ah, width, height, border, bitDepth > 8) { BitDepth = bitDepth };
        for (int p = 1; p < numPlanes; p++)
            Planes[p] = new AomYv12Plane(aw >> ssX, ah >> ssY, (width + ssX) >> ssX, (height + ssY) >> ssY, border, bitDepth > 8) { BitDepth = bitDepth };
    }

    /// <summary>Gives every plane's buffer back to AomBufferPool (the frame is unusable afterwards).</summary>
    public void Release() { foreach (var p in Planes) p.Release(); }

    public AomYv12 CloneGeometry() => new(Width, Height, SsX, SsY, NumPlanes, Planes[0].Border, BitDepth);

    public AomYv12 Clone()
    {
        var c = CloneGeometry();
        for (int p = 0; p < NumPlanes; p++) c.Planes[p].CopyFrom(Planes[p]);
        return c;
    }
}
