using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImage.Formats.Av1;

/// <summary>
/// Stable identities for the CDF arrays of an <see cref="Av1CdfContext"/>, so a coded symbol can be recorded as
/// (array id, offset, symbol) and re-coded later against a different context (row-parallel encoding: each superblock
/// row decides with its own CDF copy, then every row's symbols are re-coded in order against the real adaptive CDFs).
/// Ids are the arrays' positions in a fixed reflection walk, identical for every context. The recording context's
/// arrays are pinned so a span's address maps back to its array.
/// </summary>
internal sealed class Av1CdfIndex
{
    private readonly nint[] starts;     // sorted array start addresses
    private readonly int[] lengths, ids;

    private Av1CdfIndex(List<ushort[]> arrays)
    {
        var entries = new List<(nint Start, int Len, int Id)>(arrays.Count);
        for (int i = 0; i < arrays.Count; i++)
            entries.Add((AddressOf(arrays[i]), arrays[i].Length, i));
        entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        starts = entries.Select(e => e.Start).ToArray();
        lengths = entries.Select(e => e.Len).ToArray();
        ids = entries.Select(e => e.Id).ToArray();
    }

    private static unsafe nint AddressOf(ushort[] a) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a));

    /// <summary>A context whose arrays are pinned (the given one's values), with its index.</summary>
    internal static (Av1CdfContext Cdf, Av1CdfIndex Index) CreatePinned(Av1CdfContext from)
    {
        var c = new Av1CdfContext();
        Walk(c, pin: true, null);
        var arrays = new List<ushort[]>();
        Walk(c, pin: false, arrays);
        var src = Arrays(from);
        for (int i = 0; i < src.Length; i++) Array.Copy(src[i], arrays[i], src[i].Length);
        return (c, new Av1CdfIndex(arrays));
    }

    /// <summary>Every CDF array of the context, in id order.</summary>
    internal static ushort[][] Arrays(Av1CdfContext c)
    {
        var arrays = new List<ushort[]>();
        Walk(c, pin: false, arrays);
        return arrays.ToArray();
    }

    /// <summary>The (id, element offset) of the array a span of this context starts in.</summary>
    internal unsafe (int Id, int Offset) Locate(ReadOnlySpan<ushort> span)
    {
        nint p = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= p) lo = mid; else hi = mid - 1;
        }
        long off = (p - starts[lo]) / sizeof(ushort);
        if (p < starts[lo] || off >= lengths[lo]) throw new InvalidOperationException("CDF span is not in the indexed context.");
        return (ids[lo], (int)off);
    }

    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();

    private static FieldInfo[] Fields(Type t)
    {
        lock (FieldCache)
        {
            if (!FieldCache.TryGetValue(t, out var f))
                FieldCache[t] = f = t.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.MetadataToken).ToArray();
            return f;
        }
    }

    // Visits the context's CDF arrays in a fixed order (declaration order, recursing into the sub-contexts); with pin,
    // replaces each with a pinned array of the same size.
    private static void Walk(object o, bool pin, List<ushort[]>? into)
    {
        foreach (var f in Fields(o.GetType()))
        {
            object? v = f.GetValue(o);
            if (v is ushort[] a)
            {
                if (pin) f.SetValue(o, a = GC.AllocateArray<ushort>(a.Length, pinned: true));
                into?.Add(a);
            }
            else if (v is ushort[][] aa)
            {
                for (int i = 0; i < aa.Length; i++)
                {
                    if (pin) aa[i] = GC.AllocateArray<ushort>(aa[i].Length, pinned: true);
                    into?.Add(aa[i]);
                }
            }
            else if (v != null && f.FieldType.IsClass && f.FieldType.Namespace == typeof(Av1CdfContext).Namespace)
                Walk(v, pin, into);
        }
    }
}
