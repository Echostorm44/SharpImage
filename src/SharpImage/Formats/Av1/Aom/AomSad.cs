using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

// The 8-bit block distortion kernels libaom's motion search dispatches through cpi->ppi->fn_ptr[bsize] (aom_dsp
// sad / sad_skip / variance): all integer-exact, so the SIMD versions libaom runs and these agree bit for bit.
internal static class AomSad
{
    /// <summary>aom_sad{w}x{h}: the sum of absolute differences.</summary>
    public static uint Sad(byte[] src, int srcOff, int srcStride, byte[] refBuf, int refOff, int refStride, int w, int h)
    {
        ref byte s0 = ref MemoryMarshal.GetArrayDataReference(src);
        ref byte r0 = ref MemoryMarshal.GetArrayDataReference(refBuf);
        if (Sse2.IsSupported && w >= 8)
        {
            var acc = Vector128<ulong>.Zero;
            if (w == 8)
            {
                for (int y = 0; y < h; y++)
                {
                    var a = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s0, srcOff + y * srcStride))).AsByte();
                    var b = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r0, refOff + y * refStride))).AsByte();
                    acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(a, b).AsUInt64());
                }
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    int so = srcOff + y * srcStride, ro = refOff + y * refStride;
                    for (int x = 0; x < w; x += 16)
                    {
                        var a = Vector128.LoadUnsafe(ref Unsafe.Add(ref s0, so + x));
                        var b = Vector128.LoadUnsafe(ref Unsafe.Add(ref r0, ro + x));
                        acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(a, b).AsUInt64());
                    }
                }
            }
            return (uint)(acc.GetElement(0) + acc.GetElement(1));
        }
        uint sad = 0;
        for (int y = 0; y < h; y++)
        {
            int so = srcOff + y * srcStride, ro = refOff + y * refStride;
            for (int x = 0; x < w; x++) sad += (uint)Math.Abs(src[so + x] - refBuf[ro + x]);
        }
        return sad;
    }

    /// <summary>aom_sad_skip_{w}x{h}: twice the SAD of the even rows.</summary>
    public static uint SadSkip(byte[] src, int srcOff, int srcStride, byte[] refBuf, int refOff, int refStride, int w, int h)
        => 2 * Sad(src, srcOff, 2 * srcStride, refBuf, refOff, 2 * refStride, w, h / 2);

    /// <summary>aom_variance{w}x{h}: returns sse - sum^2 / (w h); sse out.</summary>
    public static uint Variance(byte[] src, int srcOff, int srcStride, byte[] refBuf, int refOff, int refStride, int w, int h, out uint sse)
    {
        long sum = 0;
        ulong sq = 0;
        for (int y = 0; y < h; y++)
        {
            int so = srcOff + y * srcStride, ro = refOff + y * refStride;
            for (int x = 0; x < w; x++)
            {
                int d = src[so + x] - refBuf[ro + x];
                sum += d;
                sq += (ulong)(d * d);
            }
        }
        sse = (uint)sq;
        int bits = System.Numerics.BitOperations.Log2((uint)(w * h));
        return sse - (uint)((sum * sum) >> bits);
    }
}
