using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Sample access for kernels written once over the pixel type, after libaom's lowbd / highbd split: 8-bit content lives in
/// byte planes and buffers (half the memory traffic and cache footprint), 10/12-bit in ushort. TP is byte or ushort; each
/// JIT specialisation folds the <c>typeof(TP) == typeof(byte)</c> tests away. Arithmetic is the same in both: 16-bit
/// vector lanes (bytes widened on load and narrowed with an unsigned saturating pack on store, as the lowbd SIMD does), so
/// the byte instantiation at bit depth 8 gives bit-identical results to the ushort one.
/// </summary>
internal static class Px
{
    public static bool IsByte<TP>() where TP : unmanaged => typeof(TP) == typeof(byte);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int I<TP>(TP v) where TP : unmanaged
        => typeof(TP) == typeof(byte) ? Unsafe.As<TP, byte>(ref v) : Unsafe.As<TP, ushort>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TP T<TP>(int v) where TP : unmanaged
    {
        if (typeof(TP) == typeof(byte)) { byte b = (byte)v; return Unsafe.As<byte, TP>(ref b); }
        ushort u = (ushort)v; return Unsafe.As<ushort, TP>(ref u);
    }

    /// <summary>16 samples at s[i..] as 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<ushort> Load16<TP>(ReadOnlySpan<TP> s, int i) where TP : unmanaged
    {
        ref TP r = ref MemoryMarshal.GetReference(s.Slice(i, 16));
        if (typeof(TP) == typeof(byte))
        {
            var b = Vector128.LoadUnsafe(ref Unsafe.As<TP, byte>(ref r));
            return Avx2.IsSupported ? Avx2.ConvertToVector256Int16(b).AsUInt16() : Vector256.Create(Vector128.WidenLower(b), Vector128.WidenUpper(b));
        }
        return Vector256.LoadUnsafe(ref Unsafe.As<TP, ushort>(ref r));
    }

    /// <summary>Stores 16 lanes (each within the sample range) to d[i..].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store16<TP>(Span<TP> d, int i, Vector256<ushort> v) where TP : unmanaged
    {
        ref TP r = ref MemoryMarshal.GetReference(d.Slice(i, 16));
        if (typeof(TP) == typeof(byte))
        {
            var n = Sse2.IsSupported ? Sse2.PackUnsignedSaturate(v.GetLower().AsInt16(), v.GetUpper().AsInt16()) : Vector128.Narrow(v.GetLower(), v.GetUpper());
            n.StoreUnsafe(ref Unsafe.As<TP, byte>(ref r));
            return;
        }
        v.StoreUnsafe(ref Unsafe.As<TP, ushort>(ref r));
    }

    /// <summary>8 samples at s[i..] as 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> Load8<TP>(ReadOnlySpan<TP> s, int i) where TP : unmanaged
    {
        ref TP r = ref MemoryMarshal.GetReference(s.Slice(i, 8));
        if (typeof(TP) == typeof(byte))
        {
            var b = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<TP, byte>(ref r))).AsByte();
            return Sse41.IsSupported ? Sse41.ConvertToVector128Int16(b).AsUInt16() : Vector128.WidenLower(b);
        }
        return Vector128.LoadUnsafe(ref Unsafe.As<TP, ushort>(ref r));
    }

    /// <summary>Stores 8 lanes (each within the sample range) to d[i..].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store8<TP>(Span<TP> d, int i, Vector128<ushort> v) where TP : unmanaged
    {
        ref TP r = ref MemoryMarshal.GetReference(d.Slice(i, 8));
        if (typeof(TP) == typeof(byte))
        {
            var n = Sse2.IsSupported ? Sse2.PackUnsignedSaturate(v.AsInt16(), v.AsInt16()) : Vector128.Narrow(v, v);
            Unsafe.WriteUnaligned(ref Unsafe.As<TP, byte>(ref r), n.AsUInt64().ToScalar());
            return;
        }
        v.StoreUnsafe(ref Unsafe.As<TP, ushort>(ref r));
    }

    /// <summary>Stores 8 int lanes (each within the sample range) to d[i..].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store8<TP>(Span<TP> d, int i, Vector256<int> v) where TP : unmanaged
        => Store8(d, i, Vector128.Narrow(v.GetLower().AsUInt32(), v.GetUpper().AsUInt32()));

    /// <summary>Stores 4 lanes (each within the sample range) to d[i..].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store4<TP>(Span<TP> d, int i, Vector128<ushort> v) where TP : unmanaged
    {
        ref TP r = ref MemoryMarshal.GetReference(d.Slice(i, 4));
        if (typeof(TP) == typeof(byte))
        {
            var n = Sse2.IsSupported ? Sse2.PackUnsignedSaturate(v.AsInt16(), v.AsInt16()) : Vector128.Narrow(v, v);
            Unsafe.WriteUnaligned(ref Unsafe.As<TP, byte>(ref r), n.AsUInt32().ToScalar());
            return;
        }
        Unsafe.WriteUnaligned(ref Unsafe.As<TP, byte>(ref r), v.AsUInt64().ToScalar());
    }

    /// <summary>8 samples at r as 32-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> Load8x32<TP>(ref TP r) where TP : unmanaged
    {
        if (typeof(TP) == typeof(byte))
            return Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<TP, byte>(ref r))).AsByte());
        return Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref Unsafe.As<TP, ushort>(ref r)));
    }

    /// <summary>Stores 8 int lanes (each within the sample range) at r.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store8x32<TP>(ref TP r, Vector256<int> v) where TP : unmanaged
    {
        var n = Vector128.Narrow(v.GetLower().AsUInt32(), v.GetUpper().AsUInt32());
        if (typeof(TP) == typeof(byte))
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<TP, byte>(ref r), Sse2.PackUnsignedSaturate(n.AsInt16(), n.AsInt16()).AsUInt64().ToScalar());
            return;
        }
        n.StoreUnsafe(ref Unsafe.As<TP, ushort>(ref r));
    }
}
